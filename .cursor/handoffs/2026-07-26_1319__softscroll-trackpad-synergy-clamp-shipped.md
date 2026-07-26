# Handoff: softscroll-trackpad-synergy-clamp-shipped

- created: 2026-07-26T13:19:01-0700
- created_by: cursor
- branch: main @ a1002f4

## State (MAX 5 bullets)
- Synergy trackpad scroll feel shipped: notch-clamp + 400ms sticky floor + measured defaults (scale 0.12, trackpad gap 60ms).
- Pushed to `personal` (`mlawrie96/ml-soft-scroll`) only — not `origin`. Live exe at `%LocalAppData%\SoftScroll\` with diag off.
- User confirmed trackpad “much more reasonable”; Synergy devices passable; wired still slightly better on flicks (expected — injected has no accel).
- Learnings in `lessons.md` + agent brief for MacBook scroll work.
- 4-finger quarantine path untouched; quarantine worktree still separate.

## Risks (MAX 3 bullets, or "None")
- Fresh installs get new C# defaults; this machine’s settings.json already matched — other machines with old JSON keep their values until edited.
- CI auto-release may land a version-bump commit after push — merge `personal/main` if local diverges.
- Mac-side custom scroll intercept still backlog if daily use finds trackpad insufficient.

## Next (exact commands)
# 1) git fetch personal; git merge personal/main -m "Merge auto-release version bump from CI"; git push personal main
# 2) Optional: if trackpad feels shy after daily use, try InjectedWheelScale 0.15 in %APPDATA%\SoftScroll\settings.json
# 3) MacBook mouse scroll agent: read .cursor/docs/summaries/2026-07-26-synergy-scroll-agent-brief.md

## If stuck
- git status -sb && git rev-parse --short HEAD  (PowerShell: run each command separately or use ;)
- ./.cursor/bin/ml-handoff check
- Read lessons.md entry "2026-07-26 — Trackpad-over-Synergy"
