---
paths:
  - "scripts/**"
  - "installer/**/*.sh"
  - "**/*.sh"
---
# Shell/script rules

- Use `set -Eeuo pipefail` for non-trivial Bash unless documented otherwise.
- Quote variables and validate required inputs/commands.
- Make destructive operations explicit and opt-in.
- Provide `--help` for user-facing scripts.
- Prefer dry-run/plan behavior for infrastructure operations.
- Make repeated execution safe where practical.
- Never echo secrets.
- Return non-zero on failed validation.
- Pass ShellCheck.
