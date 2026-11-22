---
topic: HomeLab Pulse UI — status, Figma source, how screens are imported, build rules
sources:
  - apps/homelab-pulse/README.md
  - apps/homelab-pulse/agent/README.md
  - apps/homelab-pulse/contracts/telemetry-sample.v1.schema.json
  - apps/homelab-pulse/contracts/hardware-profile.v1.schema.json
  - docs/roadmap.md (M5)
  - the Figma file below (public thumbnail only; the frames have not been read)
verified: 2026-11-22
load-when: Planning, building or reviewing any part of the Pulse web or mobile UI.
---

# HomeLab Pulse UI

## Status on 2026-11-22

- **UI code:** none yet.
- **Agent, contracts, Ansible role:** written and unit-tested (`apps/homelab-pulse/`).
  No machine runs the agent yet.
- **Receiver:** the .NET receiver (history, retention, public projection, SignalR) is
  developed outside this repository. It is not under `apps/`.
- **Design:** the Figma file *HomeLab Pulse — Web & Mobile*. **Its frames have not been
  read.** Figma's API refuses a request without a token (HTTP 403, checked 2026-11-22),
  no Figma connector was available in that session, and the public thumbnail (640 px)
  shows only that the file holds dark dashboard pages, not what is on them. Build
  nothing that claims to follow the screens until they are imported.

## Sources of truth

- **Layout, copy, colour, type, spacing:** the Figma file,
  `https://www.figma.com/design/HHEfGVx2SWw6VjFVVk8mQm/HomeLab-Pulse-%E2%80%94-Web---Mobile`
  (file key `HHEfGVx2SWw6VjFVVk8mQm`). The editable frames are the design; PNG
  snapshots, if committed, are visual references only.
- **Data:** the contracts in `apps/homelab-pulse/contracts/`, mapped to widgets in
  [pulse-ui-data.md](pulse-ui-data.md). A screen that needs a value the contract does
  not carry is recorded as a gap; the UI does not invent the value.

The repository is public and this file links the Figma file. The file is link-viewable
today (its thumbnail loads without signing in); restrict it if the owner does not want
the design public.

## Importing the screens

Three ways, best first:

1. **Figma connector in Claude** (claude.ai, Settings, Connectors). No secret on disk.
2. **A read-only personal access token.** The owner creates it with the *File content*
   scope read-only and saves it themselves in `~/.figma-token` (mode 600, outside the
   repository, never in chat). Claude reads it from the file without printing it and
   uses `GET /v1/files/{key}` for the structure and `GET /v1/images/{key}` for PNG
   snapshots. The owner revokes the token when the import is done.
3. **PNG exports** by the owner into a folder. Claude reads the images. This gives no
   structure and no exact colour or type values.

The import writes `pulse-ui-screens.md`: one row per frame with its name, node id,
size, web or mobile, route, the data fields it needs (from `pulse-ui-data.md`), the
states it shows (loading, empty, stale, unavailable) and any gap.

## Rules for building from the screens

1. Follow the frames. Where a frame has no mobile variant, implement it responsively
   and say so; do not invent a design.
2. The contract limits the data. Record a gap instead of faking a value.
3. Never show `unavailable` or `stale` data as current. Show the status and the
   observation time. A missing reading is not zero.
4. Derive rates from counter deltas inside one series. Start a new series after a
   reboot (`bootId` changes) or a statistics reset.
5. Show roles from data (`inRecovery` for PostgreSQL), never from a machine name:
   roles move on failover.
6. The agent never sends hostnames, device paths, serial numbers or addresses, and the
   UI must not ask for them. Anything committed to this public repository, including
   screenshots and demo data, uses synthetic values.

## Open decisions (the owner's)

- The UI stack, and where its code lives: this repository under `apps/homelab-pulse/`,
  or with the receiver.
- What the public projection may show, and whether there is a private view.
- Whether Pulse replaces the Prometheus and Grafana item in `docs/roadmap.md` (M5).
  The README already calls Pulse the monitoring; the roadmap still plans Grafana.
