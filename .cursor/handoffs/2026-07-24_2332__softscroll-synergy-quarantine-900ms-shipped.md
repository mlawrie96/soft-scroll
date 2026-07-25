# Handoff: softscroll-synergy-quarantine-900ms-shipped

- created: 2026-07-24T23:32:54-0700
- created_by: cursor
- branch: main @ a5c5899

## State (MAX 5 bullets)
- SoftScroll running from `%LocalAppData%\SoftScroll\` (republished tonight); AHK gesture listener on `:19847` (pid refreshed).
- Quarantine DurationMs=900; AHK writes `A_TickCount+900` to AppData deadline file + SetEvent.
- Verified: event+file arm; 8/8 simulated SendInput injected wheels dropped (log evidence).
- soft-scroll `a5c5899` + homelab-windows-clients `53798c8` pushed to origin/main.
- Physical 4-finger smoke still needs user confirm (Mac→Windows Synergy path).

## What Changed
- Extended quarantine 450→900ms; async drop-count logging in `InjectedWheelQuarantine`.
- AHK duration aligned; SoftScroll was found not running at session start (started + republished).
- Plan/lessons updated.

## Risks (MAX 3 bullets, or "None")
- 900ms may still be short if Synergy residual lasts longer — check log `dropped N injected` after gesture.
- SoftScroll not in HKCU Run would leave quarantine dead after reboot (verify “Start with Windows”).
- AHK allowlists only Mac LAN IPs — localhost TCP smoke cannot exercise Dispatch.

## Next (exact commands)
# 1) User: focus Windows → 2-finger scroll smooth; 4-finger up → Task View, no page jump
# 2) If jump: Get-Content "$env:APPDATA\SoftScroll\logs\softscroll-*.log" -Tail 40 | Select-String Quarantine
# 3) If needed: raise DurationMs + AHK +900 further, republish SoftScroll

## If stuck
- git status -sb; git rev-parse --short HEAD
- Confirm SoftScroll process + Listen :19847
- ./.cursor/bin/ml-handoff check
