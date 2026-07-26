# Agent brief: Synergy scroll feel (Windows SoftScroll learnings → MacBook)

**Date:** 2026-07-26  
**Source machine:** Windows PC running SoftScroll (`ml-soft-scroll`), receiving input from Mac Studio via Synergy/Deskflow  
**Audience:** Agent optimizing scroll feel on a **MacBook** (Synergy client), especially mouse; trackpad may be handled separately  
**Repo that fixed Windows side:** `C:\Users\mlawr\Documents\mlawrie\ml-soft-scroll` (canonical remote: `personal` → `mlawrie96/ml-soft-scroll`)

---

## Problem statement (what was broken)

Over Synergy, scroll from Mac Studio → Windows felt wrong in different ways for different devices:

| Source | Symptom after early fixes |
|---|---|
| Mouse plugged into Windows | Fine (baseline) |
| Mouse on Mac → Synergy → Windows | Was oversensitive / misaligned; **now feels ≈ wired mouse** |
| Trackpad on Mac → Synergy → Windows | Still much faster than either mouse; classification improved but not enough |

User also reports scroll feel problems on **MacBook as Synergy client** — may share root causes (protocol + timing), but SoftScroll itself is **Windows-only** and will not fix MacBook.

---

## Critical facts about Synergy/Deskflow (do not re-derive blindly)

Verified by reading Deskflow/Synergy source (`OSXScreen.mm`, wheel message path):

1. **No device-type signal on the wire.** macOS capture does **not** read `kCGScrollWheelEventIsContinuous`. `kMsgDMouseWheel` carries only `xDelta`/`yDelta`. You cannot tell trackpad vs mouse from an explicit flag.
2. **No original timestamps on the wire.** Client drains buffered messages in a tight loop on socket-readable. Network jitter → multiple physical notches arrive on the client as a near-simultaneous burst.
3. **Windows-local arrival time ≠ physical scroll speed** for injected input. Any acceleration that keys off inter-event gaps on the *receiving* machine will overscroll on bursts.
4. **Synergy may pre-scale** scroll (server `getScrollSpeed()`, client `XScrollScale`/`YScrollScale`). Compounding matters.
5. On Windows SoftScroll path: injected events have `LLMHF_INJECTED`. SoftScroll must **not** blanket-ignore all injected input (that breaks Synergy entirely). It stamps its own `SendInput` with `OWN_INPUT_SIGNATURE` and only skips those.

---

## What SoftScroll does today (Windows) — architecture

```
Mac trackpad/mouse
  → Synergy server (macOS)
  → Synergy client (Windows) injects wheel
  → SoftScroll WH_MOUSE_LL hook
       ├── own SendInput (signature) → ignore (no feedback loop)
       ├── quarantine window (4-finger gesture residual) → drop injected briefly
       └── else → SmoothScrollEngine.RegisterNotch(isInjected=true)
```

### Injected-specific rules in `Axis.RegisterNotch`

1. **No acceleration ramp** for injected (`AccelFactor = 1` always). Arrival clustering must not look like “user scrolling.”
2. **No momentum velocity** from injected timing.
3. **Per-notch pixel ceiling:** `clamp(px, ±StepSizePx * AccelerationMax)`.
4. **Rate-adaptive scale** (infers trackpad vs mouse from EWMA of inter-arrival gaps):
   - gap ≥ `InjectedMouseLikeGapMs` (80) → scale `1.0`
   - gap ≤ `InjectedTrackpadLikeGapMs` (tuned to **60** on this machine; default was 20) → scale `InjectedWheelScale` (tuned to **0.12**; was 0.3 then 1.0 flat)
   - linear blend between
5. **Diagnostics:** `DiagnosticWheelLogging` enqueues `[WheelDiag]` samples off the hook thread (never sync-log from `WH_MOUSE_LL`).

### Measured numbers on this Windows setup (Notepad, no native smooth scroll)

**Synergy mouse ≈ wired mouse** after rate-adaptive scale (both ~900–1200 \|px\|/s in calm tests).

**Synergy packet sizes (important):**

| Source | Typical `delta` per message | Meaning |
|---|---|---|
| Wired mouse on Windows | `120` | 1 notch |
| Synergy mouse | `720` | **6 notches packed** |
| Synergy trackpad | `720` common; bursts to thousands | multi-notch + ~50 evt/s |

**Trackpad after floor 0.12 + gap 60ms:**

- Floor engaged most of the time (~80%+)
- Steady trackpad still ~50 evt/s × ~88 px/event ≈ 4500 \|px\|/s
- Brief pauses let EWMA look “mouse-like” → scale snaps to 1.0 → next fat burst undamped (felt as jumps)
- So: **classification mostly works; remaining pain is fat deltas × high rate + pause spikes**

---

## What worked for mice (transferable lessons)

These are the lessons most useful for a MacBook agent working on **mouse** scroll over Synergy:

1. **Do not use client-local inter-event time as physical speed** for Synergy-delivered scroll. Disable or heavily damp any acceleration that assumes gaps ≈ user intent.
2. **Prefer behavior inference or explicit user control over hoping for a device flag** — the flag is not in the protocol.
3. **Synergy scroll speed settings** (server + client multipliers) are a free first lever before writing code.
4. **Measure, don’t guess:** log per-event `delta`, inter-arrival gap, and any scale/accel you apply. Compare mouse-direct vs mouse-via-Synergy vs trackpad-via-Synergy in the same app with no native smooth scrolling.
5. **Sign / reverse direction** may differ for injected vs hardware (SoftScroll has separate `ReverseInjectedWheelDirection` for Mac→Windows). MacBook may need an analogous reverse or Synergy direction setting.
6. **Fat `delta` packing:** even “mouse via Synergy” often arrives as `delta=720` not `120`. If the Mac scroll stack treats each CGEvent as one line/notch without normalizing magnitude, Synergy mouse can feel 6× hot relative to local mouse. **Normalizing per-event magnitude** may matter as much as rate.

---

## What did *not* fully fix trackpad (Windows)

- Flat `InjectedWheelScale` alone: hurts Synergy mouse when shared with trackpad.
- Rate-adaptive scale alone: gets mice right; trackpad still hot because of multi-notch packets + 50 Hz stream + EWMA unstick after pauses.

**In-flight SoftScroll follow-up (same session):** when stream looks trackpad-like, (a) clamp each message to ≤1 notch before scale, (b) sticky trackpad mode ~400ms so short pauses don’t unlock full-scale bursts. Mice at scale 1.0 keep full delta magnitude.

---

## Mac-side / multi-gesture intercept option (future, heavier)

Already exists for **4-finger** gestures (not 2-finger scroll):

```
Mac Studio trackpad 4-finger
  → BetterTouchTool → gesture-forward script
  → Windows AHK :19847 → Task View / desktops
  → AHK signals SoftScroll quarantine (~900ms) so Synergy residual wheel doesn’t jump the page
```

**Custom 2-finger scroll path** (intercept on Mac, don’t rely on Synergy wheel) is possible but is a second input stack:

- Capture scroll on Mac Studio (BTT / CGEvent tap)
- Suppress or zero Synergy’s scroll forwarding for that gesture
- Send normalized deltas to Windows (new SoftScroll listener) **and** somehow to MacBook
- MacBook cannot use SoftScroll; needs its own receiver or Synergy still in the path

**Recommendation:** try SoftScroll notch-clamp + sticky first on Windows; use Synergy scroll-speed + no-accel-on-injected lessons on MacBook for mouse. Only build a custom scroll pipe if both fail.

---

## Files / settings reference (Windows SoftScroll)

| Path | Role |
|---|---|
| `Core/SmoothScrollEngine.cs` | `RegisterNotch`, `ComputeInjectedScale`, diag queue |
| `Hooks/GlobalMouseHook.cs` | Injected filter via `OWN_INPUT_SIGNATURE` |
| `Core/InjectedWheelQuarantine.cs` | 4-finger residual drop (separate feature — don’t break it) |
| `Settings/AppSettings.cs` | `InjectedWheelScale`, `InjectedMouseLikeGapMs`, `InjectedTrackpadLikeGapMs`, `DiagnosticWheelLogging` |
| `%APPDATA%\SoftScroll\settings.json` | Live tuned values |
| `%APPDATA%\SoftScroll\logs\softscroll-*.log` | `[WheelDiag]` lines when diag on |
| `lessons.md` | Decision log (injected skip, accel, rate-adaptive scale) |

**Current live tune (this Windows box):**

```json
"InjectedWheelScale": 0.12,
"InjectedMouseLikeGapMs": 80,
"InjectedTrackpadLikeGapMs": 60,
"DiagnosticWheelLogging": true,
"ReverseInjectedWheelDirection": true
```

---

## Suggested checklist for MacBook mouse scroll agent

1. Confirm topology: which machine is Synergy **server** vs **client**; which device generates the scroll.
2. Check Synergy/Deskflow scroll speed multipliers on server and client; try reducing before code.
3. Find any app or utility applying scroll acceleration from inter-event timing on the MacBook; disable for Synergy-sourced events if distinguishable.
4. Log CGEvent / wheel deltas for: local mouse, Synergy mouse, Synergy trackpad — compare magnitude and rate (expect Synergy to look “fatter” and/or burstier).
5. If events are continuous (`kCGScrollWheelEventIsContinuous` / pixel-based) locally but Synergy delivers line deltas, normalize explicitly.
6. Do **not** assume a device-type bit survived Synergy — it didn’t leave the Mac capture path.
7. Keep 4-finger gesture path separate from 2-finger scroll; don’t reintroduce AHK `Wheel*` hotkey mutes (they previously froze the gesture listener via `MaxHotkeysPerInterval`).

---

## One-paragraph summary for context windows

Synergy’s wheel protocol has no trackpad/mouse flag and no timestamps; the Windows SoftScroll fix for mice was: never accelerate injected input from arrival gaps, reverse injected direction independently, and rate-adapt a dampening scale so slow Synergy mouse stays at 1.0 while fast streams dampen. That made Synergy mouse ≈ wired mouse. Trackpad remains hot because Synergy packs ~6+ notches per message at ~50 Hz and brief pauses unstick the classifier — next SoftScroll change clamps trackpad-like messages to 1 notch and keeps dampening sticky ~400ms. MacBook cannot use SoftScroll; apply the same protocol facts (no device bit, no timestamps, fat deltas, no arrival-time accel) there, and only consider a custom Mac→clients scroll pipe if settings + local normalization fail.
