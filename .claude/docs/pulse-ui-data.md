---
topic: HomeLab Pulse telemetry — what each field can show and how to treat it
sources:
  - apps/homelab-pulse/contracts/telemetry-sample.v1.schema.json
  - apps/homelab-pulse/contracts/hardware-profile.v1.schema.json
  - apps/homelab-pulse/agent/README.md
  - ansible/roles/pulse_agent/templates/ (timers)
  - docs/roadmap.md (M5 alerts)
verified: 2026-11-22 (contract v1, repository at commit ecd0504)
load-when: Mapping a screen or widget to data, or deciding how to show missing, stale, reset or duplicate readings.
---

# What the UI can show

## What arrives

One sample per machine every 15 seconds: `schemaVersion` 1, `sampleId` (UUID),
`observedAt` (UTC, milliseconds, `Z`), `kind` `host`, and `values`. A sample is at most
64 KiB. It carries no machine name: the receiver knows the machine from the bearer
credential.

Delivery is at least once and can be late:

- **Duplicates.** Deduplicate by machine and `sampleId`.
- **Order.** Sort by `observedAt`, not by arrival.
- **Backfill.** After an outage the agent replays what it queued (at most 512 samples
  or 24 hours). Expect old samples to arrive late, and a permanent gap for anything
  older.
- **Repeated readings.** A probe reading is reused for 30 seconds with its original
  `observedAt`, so consecutive samples can carry the same one. Plot a reading once, at
  its own `observedAt`.
- **SMART.** Snapshots are produced every 60 seconds.

## Host values (always present)

| Field | Shape | Use |
|---|---|---|
| `cpuTimesSeconds` | cumulative seconds: user, nice, system, idle, iowait, irq, softirq, steal | Busy % and iowait % from the deltas of two samples. iowait is the signal that matters here: disk I/O is the platform's ceiling |
| `memory` | bytes: totalBytes, availableBytes, swapTotalBytes, swapFreeBytes | Used = total − available; swap used = swapTotal − swapFree |
| `loadAverage` | three numbers: 1, 5, 15 minutes | |
| `uptimeSeconds` | seconds | |
| `bootId` | UUID | A new value means a reboot: counters restarted, start a new series |
| `filesystems` | alias → totalBytes, availableBytes | Used and free per alias |
| `network` | alias → rxBytes, txBytes (cumulative) | Throughput from deltas |
| `temperaturesCelsius` | alias → °C | Can be empty |

## Optional values (omitted when the machine does not configure them)

| Field | Shape | Use |
|---|---|---|
| `fanSpeedsRpm` | alias → RPM | Zero is a real reading |
| `diskSmart` | alias → reading with `status` `available`, `stale` or `unavailable` | `available`: `observedAt` plus at least one of the counters the drive reports (NVMe and SATA differ): `temperatureCelsius`, `healthPassed`, `nvmePercentageUsed`, `nvmeMediaErrors`, `reallocatedSectors`, `pendingSectors`, `offlineUncorrectableSectors`. `stale` (snapshot older than 3 minutes): `observedAt` only. `unavailable`: `status` only, not even a time |

## Service probes (one per machine; each is omitted when not configured)

Every reading has `status` (`available` or `unavailable`) and `observedAt`. An
`unavailable` reading carries only those two.

| Field | Values | Use |
|---|---|---|
| `nginx` | `activeConnections`, `readingConnections`, `writingConnections`, `waitingConnections`; counters `acceptedConnectionsTotal`, `handledConnectionsTotal`, `requestsTotal` | Requests per second from the delta of `requestsTotal`. Handled below accepted means dropped connections |
| `sqliteStorage` | alias → `databaseBytes`, `walBytes`, `sharedMemoryBytes`, `rollbackJournalBytes`, `totalBytes` | Sizes from file metadata, a point-in-time estimate. No query is run |
| `redis` | `usedMemoryBytes` (always when available); optional `maxMemoryBytes` (0 means no cap), `operationsPerSecond`, `connectedClients`, `aofCurrentSizeBytes` (only with AOF); counters `keyspaceHitsTotal`, `keyspaceMissesTotal`, `evictedKeysTotal` | Hit ratio from the deltas of hits and misses within one server lifetime |
| `postgresql` | `inRecovery`, `databaseBytes`, `connectedBackends` (the probe counts as one); counters `commitsTotal`, `rollbacksTotal`, `bufferHitsTotal`, `blocksReadTotal`, `deadlocksTotal`; optional `statsResetAt` | `inRecovery` true means a standby: take the role from here, not from a name. A new `statsResetAt` starts a new series |
| `rabbitmq` | one approved queue: `messages`, `messagesReady`, `messagesUnacknowledged`, `consumers` | Zero is a reported zero. `unavailable` is not zero |

## Reading states

| Status | Meaning | Show |
|---|---|---|
| `available` | the probe answered | the values and their age |
| `stale` (SMART only) | the snapshot is older than 3 minutes | the last observation time, no values, flagged |
| `unavailable` | the probe failed, timed out or is unreachable | "unavailable", never 0 |

A machine whose samples stop arriving is the receiver's concern. Show "no data since
<time>", not the last value as if it were current.

## Not in the contract (record a gap, do not fake)

- **Machine identity:** no hostname, address, serial or device path, by design. Names
  come from the receiver's catalog.
- **Redis role** (master or replica), **PostgreSQL replication lag**, **Sentinel
  quorum**, **backup age**, **Docker or container state**, **Gitea Actions state**,
  **NGINX access-log and TLS metrics**. The agent README lists the last three as
  follow-up work.
- Of the alerts planned in `docs/roadmap.md` (M5), the contract covers disk space
  (`filesystems`) and SMART. Backup age, replication lag and Sentinel quorum are not in
  it.

## Hardware profile (a separate contract)

`pulse_agent inventory` prints a profile that the owner reviews before it goes into the
dashboard catalog: machine manufacturer and model; CPU architecture, logical CPUs,
physical cores and model; OS-visible RAM and, when SMBIOS is readable, the modules
(capacity, type, rated and configured speed); disks by alias (status, capacity, model,
rotational). It is static. Use it for the hardware panels, not for charts.
