# Claude working docs

Notes that Claude Code loads on demand. They are written from evidence (the code, the
contracts, the live systems) and dated. Documentation for people (architecture, security,
runbooks, roadmap) stays in `docs/`; `CLAUDE.md` says which document owns what.

| Document | Load when |
|---|---|
| [pulse-ui.md](pulse-ui.md) | Planning or building the HomeLab Pulse web and mobile UI: its status, the Figma source, how the screens are imported, the rules for building them |
| [pulse-ui-data.md](pulse-ui-data.md) | Choosing which telemetry field feeds which widget, and how to show missing, stale, reset or duplicate readings |

Maintenance:

- A change to the Pulse contracts (`apps/homelab-pulse/contracts/`) updates
  `pulse-ui-data.md` in the same commit.
- Importing or changing the screens updates `pulse-ui.md` and creates or updates
  `pulse-ui-screens.md`, the screen catalog. That file does not exist until the import.
