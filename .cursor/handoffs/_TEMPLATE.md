# Handoff: {{SLUG}}

- created: {{CREATED_AT}}
- created_by: {{AGENT}}
- branch: {{BRANCH}} @ {{COMMIT}}

## State (MAX 5 bullets)
- …

## Risks (MAX 3 bullets, or "None")
- …

## Next (exact commands)
# 1)
# 2)

## If stuck
- git status -sb && git rev-parse --short HEAD  (PowerShell: run each command separately or use ;)
- ./.cursor/bin/ml-handoff check
