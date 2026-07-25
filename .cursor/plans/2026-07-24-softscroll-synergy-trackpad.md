# SoftScroll + Synergy trackpad scroll (master plan)

**Repo:** https://github.com/mlawrie96/soft-scroll (`ml-soft-scroll` checkout)  
**Live app:** `%LocalAppData%\SoftScroll\SoftScroll.exe`  
**Related:** Studio BTT gestures → AHK `:19847` (`homelab-windows-clients`)

## Confirmations (2026-07-24)

| Question | Answer |
|---|---|
| Skip Windows hub agent for now? | **Yes** — hub tray is optional backlog; not required for scroll/gestures. |
| Is 4-finger residual scroll “fixed” by removing Wheel mute? | **No.** Removing mute only stopped AHK from freezing. Synergy can still inject a wheel burst during vertical 4-finger; SoftScroll may amplify it. |
| Mac gesture detector name | **BetterTouchTool (BTT)** — not Hammerspoon. HS only tracks focus/health. |
| BTT restart-proof? | App may run now; **Login Item was missing** — re-added. Not launchd; macOS Login Items. Hammerspoon is separate Login Item. |
| 2-finger Synergy scroll | **Already SoftScroll’s job** — `LLMHF_INJECTED` path + reverse/accel fixes in lessons.md. Do **not** rebuild scrolling on the Mac. |

## Architecture (do not reinvent scrolling on Mac)

```
Mac Studio trackpad
  ├─ 2-finger scroll ──► Synergy ──► Windows wheel (injected)
  │                         └──────► SoftScroll WH_MOUSE_LL → smooth SendInput
  │
  └─ 4-finger swipe ──► BTT → gesture-forward-btt.sh → AHK :19847 → Task View / desktops
                           └─ Synergy STILL emits residual wheel ──► SoftScroll
                                (quarantine: AHK SetEvent → SoftScroll drops injected ~900ms)
```

**Rule:** Intercept/improve scroll **on Windows in SoftScroll**, not by replacing Synergy’s scroll path or building a Mac-side scroll engine.

## Workstreams

### A — Normal trackpad scroll feel (mostly done)
SoftScroll already:
- Hooks Synergy-injected wheel (`GlobalMouseHook` + `IsInjected`)
- Avoids accel/momentum on injected notches (`SmoothScrollEngine`)
- Optional `ReverseInjectedWheelDirection` for Mac→Windows sign

Tune in SoftScroll Settings; keep “Start with Windows” (HKCU Run — already set).

### B — 4-finger residual wheel flood (shipped)
1. SoftScroll: `InjectedWheelQuarantine` watches `Local\SoftScroll_QuarantineInjectedWheel` + deadline file; swallow **injected-only** wheel for ~900ms (`Handled=true`, no engine). Drop counts logged from watcher thread.
2. AHK: after `taskview`/`next`/`prev`, write TickCount deadline file + `SetEvent` (no Wheel hotkeys). Same clock as SoftScroll `TickCount64`.
3. Verify: focus Windows → 4-finger up → Task View without page jump; 2-finger scroll still smooth. SoftScroll must be running.

**Shipped 2026-07-24 night:** duration 900ms, drop logging, republished to `%LocalAppData%\SoftScroll\`. Simulated inject: 8/8 drops. Physical 4-finger smoke still user-confirm.

### C — Optional later
- SoftScroll heuristic: drop pathological injected bursts without AHK signal
- Synergy scroll-speed settings on Mac server (reduces flood amplitude)
- Windows hub Gestures row (separate plan) — only if you want tray UX

## Do not
- AHK `Wheel*` hotkeys / MaxHotkeys mute (broke gestures)
- HID / CGEventPost-through-Synergy
- Block SoftScroll from all injected input (breaks Synergy 2-finger scroll — see lessons.md)

## Next action
User smoke-test: focus Windows → 2-finger smooth; 4-finger up → Task View, no page jump. If jumps remain, check SoftScroll log for `dropped N injected` after gesture; extend DurationMs further if needed.
