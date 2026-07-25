## 2026-07-24 - Quarantine Synergy residual wheel during Mac 4-finger gestures

**Symptom:** 4-finger swipe up/down (BTT → AHK Task View) also dumped a Synergy
wheel burst into Windows. An AHK Wheel* mute tripped MaxHotkeysPerInterval
(~70 events/1.3s) and froze the gesture listener.

**Fix:** SoftScroll drops injected-only wheel for ~900ms when AHK signals named
event Local\SoftScroll_QuarantineInjectedWheel and/or writes TickCount deadline
to %AppData%\SoftScroll\gesture_wheel_quarantine_until.txt (A_TickCount ==
Environment.TickCount64 lower bits). Real hardware scroll untouched.
Do not reintroduce AHK Wheel hotkeys.

**Ops:** SoftScroll must be running (`%LocalAppData%\SoftScroll\SoftScroll.exe`)
or Synergy residual scroll has no quarantine. Watcher logs drop counts off the
hook thread — never log synchronously from ShouldDrop.

**Files:** Core/InjectedWheelQuarantine.cs, App.xaml.cs; AHK QuarantineSoftScrollWheel.

---
## 2026-07-24 - Quarantine duration + drop-count logging

**Why:** 450ms was short for Synergy residual after 4-finger; SoftScroll was also
found not running during verification (jumps look like “quarantine failed”).

**Change:** DurationMs 900; AHK A_TickCount+900; async drop-count flush from
watcher; file-arm logging. Verified: event+file arm + 8/8 SendInput drops.

---
# Lessons Learned

Persistent knowledge base for SoftScroll project. Read at the start of every session.

---

## 2026-07-24 — Injected-input acceleration ramp caused overscroll over Synergy

### Symptom

After the injected-event pipeline fix (previous entry), Synergy-forwarded scroll got smoothing and
correct reversal, but occasionally jumped much farther than the same physical gesture on a
direct-plugged mouse — not constant, intermittent bursts of overscroll.

### Root cause analysis

Confirmed by reading Synergy/Barrier/Input Leap/Deskflow's actual source (shared C++ codebase across
all four names): the wire protocol (`kMsgDMouseWheel`) carries only `xDelta`/`yDelta`, no timestamp.
The client's `ServerProxy::handle_data()` drains every buffered message in one unpaced synchronous
loop on each socket-readable event — so when network jitter delays a few messages, they arrive at
Soft Scroll's hook essentially simultaneously, even though the user scrolled at a normal cadence on
the server side. `Axis.RegisterNotch`'s acceleration ramp (`nowMs - LastNotchTime <= AccelerationDeltaMs`
→ ramp `AccelFactor` up to `AccelerationMax`) treats Windows-local arrival time as a proxy for
physical scroll speed — an assumption that only holds for real hardware, where the hook sees every
notch as it's actually generated. For injected input there is no floor/threshold fix: a burst can
compress N genuinely-separate notches into sub-millisecond arrival gaps regardless of what
`AccelerationDeltaMs` is set to, because the timing information was never transmitted in the first
place. Synergy also applies its own scroll-speed multiplier (macOS server's `getScrollSpeed()`, and
the client's configurable `XScrollScale`/`YScrollScale`, up to 10x) before Soft Scroll ever sees the
delta, which can compound the effect.

### The fix

1. `Axis.RegisterNotch` now takes `isInjected`. When true, `AccelFactor` is forced to `1` — injected
   notches never ramp acceleration from arrival-time clustering.
2. A hard ceiling on any single notch's pixel contribution: `Math.Clamp(rawPixels, -maxPixels,
   maxPixels)` where `maxPixels = StepSizePx * AccelerationMax` — the same maximum a single real
   hardware notch could already produce at full acceleration. No-op for hardware (can't exceed this
   today), but a safety net for injected input against any other compounding cause (KVM-side
   pre-scaling, duplicate delivery) without needing to fully diagnose which one is in play.
3. Momentum/velocity tracking (`Velocity = pixels / timeSinceLast`) also now skips injected notches,
   for the same reason — timeSinceLast for a burst can be near-zero, which would otherwise produce an
   absurd velocity value feeding into the momentum phase.

Deliberately did NOT add diagnostic logging in the hot path (`RegisterNotch` runs synchronously on the
low-level-hook callback thread; Windows can silently detach a slow-to-respond `WH_MOUSE_LL` hook, and
synchronous file I/O there risks reintroducing exactly the kind of mysterious "stopped working"
symptom this whole investigation started from). If future diagnosis needs event-level tracing, buffer
in memory and flush asynchronously off the hook thread — never log synchronously from inside
`RegisterNotch`/`HookCallback`.

### Architectural rule going forward

**Never use Windows-local arrival time as a proxy for physical input timing when the source is
injected.** Only real hardware, dispatched through the OS's own input stack, has arrival time that
faithfully tracks generation time. Any synthetic/injected source (KVMs, RDP, automation, virtual
machines) may buffer, batch, or replay with no timing fidelity, and no protocol assumption should be
made about it without checking that specific source's actual wire behavior first — as was done here
by reading Synergy/Deskflow's source directly rather than guessing.

### Files changed

- `Core/SmoothScrollEngine.cs` — `Axis.RegisterNotch` gains `isInjected` param; forces `AccelFactor=1`
  and skips momentum/velocity tracking for injected notches; adds the per-notch pixel ceiling.

---

## 2026-07-24 — AutoDisableOnTouchpad silently broken by a GetRawInputDeviceList return-value bug

### Symptom

Found incidentally while auditing logs for the Synergy overscroll issue above: every single app
launch logged `[InputDetector] Failed to enumerate devices: 21` — a warning, with the same "error
code" every time, on 5/5 sessions in the day's log.

### Root cause

`InputDeviceDetector.EnumerateDevices()` calls `GetRawInputDeviceList` twice. The first call (buffer =
`null`) correctly expects `0` on success per Win32 semantics — that call is just asking for the
required count. The second call (buffer = actual array) instead returns **the number of elements
copied** on success, not `0` — only `(UINT)-1` indicates failure. The code checked `result != 0` on
the second call too, so every successful enumeration (returning the real device count, e.g. `21`) was
misread as a failure, logged a warning, and returned early — before `_knownTouchpadHandles` /
`_knownMouseHandles` were ever populated. Net effect: `AutoDisableOnTouchpad`'s primary detection path
(`fTouchPad` via `RID_DEVICE_INFO`) was dead on every launch, silently falling back to the much weaker
timeout-based heuristic in `CheckDeviceState()`, for as long as this bug existed.

### The fix

Changed the second call's success check from `result != 0` to `result == unchecked((uint)-1)`,
matching actual Win32 semantics for a populated-buffer call.

### Files changed

- `Core/InputDeviceDetector.cs` — corrected the second `GetRawInputDeviceList` result check.

---

## 2026-07-24 — Blanket injected-event skip breaks software KVMs (Synergy/Barrier/Input Leap)

### Symptom

User reported "Reverse Wheel Direction" (and, on investigation, smoothing generally) silently doing
nothing — but only for a mouse shared over Synergy (a software KVM). The same physical mouse plugged
directly into the machine worked correctly. No settings, exclusion list entries, or driver conflicts
were involved; extensive troubleshooting (G HUB, AV/EDR, WDAC/AppLocker, Smart App Control, reinstall)
ruled out everything except Soft Scroll itself.

### Root cause analysis

`GlobalMouseHook.HookCallback` unconditionally skips **any** mouse event carrying `LLMHF_INJECTED` /
`LLMHF_LOWER_IL_INJECTED`:

```csharp
if ((data.flags & (NativeMethods.LLMHF_INJECTED | NativeMethods.LLMHF_LOWER_IL_INJECTED)) != 0)
    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
```

This exists to stop the hook from reprocessing its own `SendInput`-emitted pulses (which would
otherwise re-enter `OnWheel`/`OnHWheel` and feed back on themselves). But Windows sets the same
`LLMHF_INJECTED` flag for **any** synthetic input, not just our own — including input forwarded by
software KVMs (Synergy, Barrier, Input Leap), RDP sessions, and general automation tooling. The check
had no way to distinguish "this is my own re-emitted pulse" from "this is a real user scrolling via a
KVM," so it silently dropped both the smoothing and the `ReverseWheelDirection` handling for every KVM
user — with no error, no log entry, and no setting to disable it. This affected all three `SendInput`
emission sites that share the hook: `SmoothScrollEngine.SendWheel` (vertical wheel), `ZoomSmoothEngine.
EmitZoomViaSendInput` (Ctrl+wheel zoom), and `MiddleClickScrollEngine.SendWheel` (middle-click drag
scroll) — a KVM user got none of the app's core features, not just reversal.

### The fix

1. **Tag our own output.** Every `SendInput`-emitted `MOUSEINPUT` at all three call sites now sets
   `dwExtraInfo = NativeMethods.OWN_INPUT_SIGNATURE` (a private magic constant), so our own synthetic
   events are positively identifiable rather than merely "injected."
2. **Narrow the skip condition** in `GlobalMouseHook.HookCallback` from "skip all injected events" to
   "skip only injected events carrying our signature":
   ```csharp
   if ((data.flags & (NativeMethods.LLMHF_INJECTED | NativeMethods.LLMHF_LOWER_IL_INJECTED)) != 0
       && data.dwExtraInfo == NativeMethods.OWN_INPUT_SIGNATURE)
       return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
   ```

Third-party injected input (no matching signature) now falls through to the normal dispatch logic
below and gets full smoothing/reversal/zoom/middle-click treatment, identical to real hardware. Our
own re-emitted pulses still carry the signature and are still correctly ignored, so the feedback-loop
protection this check existed for is preserved.

### Architectural rule going forward

**A "was this injected by me" check must be a positive identity check (a signature we control), never
a property Windows assigns to all synthetic input generically.** `LLMHF_INJECTED` answers "is this
synthetic," not "did Soft Scroll create this" — those are different questions, and conflating them
silently breaks every legitimate synthetic-input source we don't personally control (KVMs, RDP,
accessibility tools, automation). Any future code that injects mouse or keyboard input via `SendInput`
must stamp `dwExtraInfo` with `OWN_INPUT_SIGNATURE` and any hook-side self-filtering must check for
that exact value, not the generic injected flag.

### Verification checklist before declaring this fix complete

1. Direct-plugged mouse: vertical wheel smoothing + reversal — unaffected, still correct (regression
   check: the old blanket skip never applied to real hardware anyway, but confirm no double-reversal
   was introduced by the narrower check).
2. Software-KVM-forwarded mouse (Synergy/Barrier/Input Leap): vertical wheel now gets smoothing +
   correct reversal, matching the direct-plugged experience.
3. Ctrl+wheel zoom and middle-click drag scroll: unaffected on direct mouse, now also work over a KVM.
4. No feedback loop / runaway scrolling on either input path (our own pulses are still filtered out).
5. `dotnet build` — 0 warnings, 0 errors.

### Files changed

- `Native/NativeMethods.cs` — added `OWN_INPUT_SIGNATURE` constant.
- `Hooks/GlobalMouseHook.cs` — narrowed the injected-event skip to require the signature match.
- `Core/SmoothScrollEngine.cs`, `Core/ZoomSmoothEngine.cs`, `Core/MiddleClickScrollEngine.cs` — stamp
  `dwExtraInfo = OWN_INPUT_SIGNATURE` on every `SendInput`-emitted `MOUSEINPUT`.

---

## 2026-07-11 — Issue #13: Shift+wheel regression introduced by horizontal scroll fix

### Symptom (regression reported by user on build from commit `be8fdc8`)

After applying the side-button horizontal-scroll direction fix (`75c4251a`), the previously-working Shift+vertical-wheel-as-horizontal stopped working: scrolling wheel up while holding Shift now scrolled right instead of left.

### Root cause analysis

The previous fix (`75c4251a`) added a single sign inversion at one point in the pipeline to make the side-button (real `WM_MOUSEHWHEEL`) direction match physical intent. But horizontal scrolling in this app has **two distinct entry points** that converge at the same `MouseHWheel` event and the same `SendWheel` emission:

1. **Real HWHEEL event** — `WM_MOUSEHWHEEL` from the side-button. Native convention: `+delta` = scroll right.
2. **Shift+vertical wheel conversion** — `WM_MOUSEWHEEL` reinterpreted as horizontal when Shift is held. Native convention: `+delta` (wheel up) = scroll left (because target windows convert Shift+wheel-up into horizontal-left).

These two paths were treated identically inside `OnHWheel` → `SendWheel`, but the **target window's interpretation of the emitted message depends on which path produced it**:

| Path | Native source delta | Meaning at source | After inversion in `SendWheel` | Meaning at target |
|---|---|---|---|---|
| Side-button | `+120` (right) | right | `-120` (Shift+wheel down) | right ✅ |
| Shift+vertical wheel up | `+120` (up) | up → intended left | `-120` (Shift+wheel down) | right ❌ |

A single inversion in `SendWheel` makes only one path correct and silently breaks the other. The architecture mistake was treating these two sign conventions as identical when they are not.

### The fix (two changes that must land together)

1. **Keep** the `(-hMouseData)` inversion in `Core/SmoothScrollEngine.cs::SendWheel`. This corrects the side-button path.
2. **Add** a matching inversion in `Hooks/GlobalMouseHook.cs::HookCallback` when forwarding a Shift+vertical wheel event to `MouseHWheel`. This compensates for the `SendWheel` inversion, restoring the original Shift+wheel direction.

Both inversions cancel out for Shift+wheel events; only the side-button path ends up with a single net inversion. Verified by tracing all 6 event combinations (vertical wheel, Shift+vertical wheel, side-button × smoothness on/off).

### Architectural rule going forward

**Never apply a single sign inversion on a path that has multiple upstream sources with different sign conventions.** If a hook callback can route to the same handler from two different Windows messages, identify them by message type at the boundary and apply per-source normalization there, not once at the shared emission point.

Apply the same rule to all four hook handlers in `App.xaml.cs` (`MouseWheel`, `MouseHWheel`, `MouseZoomWheel`, `MiddleButtonDown`). Each must early-return without setting `Handled = true` when its corresponding feature is disabled — never swallow a native event unless you have a positive path to re-emit it.

### Verification checklist before declaring any horizontal-scroll fix complete

1. Trace **side-button** direction (HWHEEL) — should match user's physical tilt.
2. Trace **Shift+vertical wheel** direction — should match Shift+MWHEEL convention at target (wheel up = left).
3. Trace **vertical wheel** without modifiers — must be unaffected.
4. Trace **Ctrl+wheel** (zoom) — must be unaffected.
5. Verify `HorizontalSmoothness = false` passes the native `WM_MOUSEHWHEEL` through unchanged.
6. Run `dotnet build` — must report 0 warnings, 0 errors.

### Engineering lesson: dual-path input translation

Whenever code translates one Windows input message into another (e.g. HWHEEL → Shift+MWHEEL, vertical wheel → Shift+MWHEEL), the sign convention of the **target** message must be re-derived from scratch at the translation site, not propagated through any shared normalization. Document both messages and their conventions in a comment at the call site so future maintainers don't reintroduce the bug while "simplifying".

### Files changed

- `Hooks/GlobalMouseHook.cs` — invert `delta` when forwarding Shift+vertical wheel to `MouseHWheel`.
- `App.xaml.cs` — early-return in `MouseHWheel` handler when `!HorizontalSmoothness`, leaving `Handled = false` so native event passes through.
- `Core/SmoothScrollEngine.cs` — invert `hMouseData` in `SendWheel` when computing `wParam` for `PostMessageW`.