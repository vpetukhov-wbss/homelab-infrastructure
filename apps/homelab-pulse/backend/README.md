# HomeLab Pulse receiver

This ASP.NET Core 10 receiver accepts telemetry from the Python agent and stores it
in SQLite and serves an allowlisted dashboard overview.

## Local development

Use the .NET SDK pinned in `global.json` and Python 3.10 or newer. Run these checks
from this directory:

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore
```

For a local demo, generate a high-entropy credential and pass only its SHA-256
hex digest to the API. Keep the original value for the Python agent. This shell
example sets the credential for a disposable local session.

```sh
AGENT_TOKEN="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
export Telemetry__Agents__demo="$(printf '%s' "$AGENT_TOKEN" | sha256sum | cut -d ' ' -f 1)"
export Telemetry__DatabasePath="$PWD/data/pulse.db"
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/HomeLab.Pulse.Api --no-launch-profile --urls http://127.0.0.1:5080
```

`Telemetry:Agents` maps each approved agent identifier to a unique credential
digest. Load credentials through the runtime configuration provider. The app
reads this registry at startup. Supply the matching raw bearer to the agent
through its existing token file or systemd credential. Agent access is intended
for loopback and the private WireGuard network.

## HTTP contract

`POST /internal/v1/telemetry` uses the existing [v1 schema](../contracts/telemetry-sample.v1.schema.json).
The API embeds that file at build time and validates the entire sample, including
the schema's UUID and date-time formats. Samples may be at most seven days old and
five minutes ahead of the receiver clock. Each request is limited to 64 KiB.

| Status | Meaning |
| --- | --- |
| `204` | The sample was committed, or its authenticated agent and sample ID were already stored. |
| `400` | Invalid JSON, duplicate JSON keys, or a body without a JSON content type. |
| `401` | Missing or invalid bearer credential. |
| `413` | Request body exceeds 64 KiB. |
| `422` | JSON violates the schema or the observation time window. |
| `503` | SQLite could not commit the sample due to an operational failure. The agent can retry. |

`GET /health/live` checks the process, and `GET /health/ready` checks that the
telemetry table is readable. `GET /openapi/v1.json` is available in Development.
The OpenAPI document describes the ingest envelope. The bundled JSON Schema is
the full wire contract.

## Storage and boundaries

One receiver process uses a SQLite file on local storage and applies the EF Core
migration before serving requests. Give the process access to a private state
directory where SQLite can create its WAL and shared-memory files. Startup stops
if the schema cannot be migrated or WAL cannot be enabled. Use the SQLite backup
API or `.backup` for a consistent backup while the receiver is running. Restore
with the receiver stopped, then verify readiness before resuming ingest.

`Telemetry/Ingest` owns authentication, HTTP mapping, schema validation and the
ingest use case. Its persistence boundary saves a sample with one parameterized,
atomic SQLite statement. The primary key is `(AgentId, SampleId)`. Observation
and receipt times are separate UTC tick values. The first accepted payload is
preserved on retry. Health and error responses contain no credentials or payloads.

The xUnit suite uses a temporary SQLite file. It covers duplicate delivery,
restart, request limits, validation, and write-lock recovery, and sends a sample
using the production Python encoder and HTTP transport through Kestrel over loopback.

## Telemetry retention

The receiver starts a retention sweep after migrations at startup and runs it
again every hour. Samples observed strictly before the cutoff are deleted oldest
first. SQLite commits each batch independently. An interrupted sweep leaves
undone batches for the next pass. The worker logs counts and SQLite error codes.

| Environment variable | Default | Accepted values |
| --- | --- | --- |
| `Telemetry__Retention__Days` | 7 | 7–30 days |
| `Telemetry__Retention__BatchSize` | 512 | 1–1024 rows |

The lower bound matches the ingest replay window. Each sweep processes at most
128 batches, so a large backlog can take multiple hours. SQLite reuses storage
freed by ordinary deletes. The database file can retain its high-water size.

## Dashboard overview

`GET /api/v1/overview.json` is anonymous and returns `Cache-Control: no-store`.
Enable publication explicitly for each agent and choose its public alias and the
fields to publish. The default catalog is empty, even when ingest has data.
Aliases must be unique, different from ingest agent IDs and suitable for public
display. The catalog is read at startup. This synthetic example extends the
local demo above.

```sh
export Dashboard__PublicNodes__demo__Alias=demo-edge
export Dashboard__PublicNodes__demo__Metrics__0=memory
export Dashboard__PublicNodes__demo__Metrics__1=cpu
export Dashboard__PublicNodes__demo__Metrics__2=disk
export Dashboard__PublicNodes__demo__Filesystem=demo-root
```

| Metric selection | Public values | Source selector |
| --- | --- | --- |
| `cpu` | CPU busy percentage | |
| `memory` | Used and total memory in bytes | |
| `swap` | Used and total swap in bytes | |
| `disk` | Available and total filesystem space in bytes | `Filesystem` |
| `network` | Receive and transmit bytes per second | `NetworkInterface` |
| `temperature` | Temperature in °C | `TemperatureSensor` |

Source selectors match the corresponding aliases in the agent configuration.
The response includes `schemaVersion`, a persisted `revision`, a UTC `generatedAt`
and the list of approved nodes. Each node has an `alias`, `status`, `observedAt`
and typed `metrics`. A node is `unavailable` before its first eligible
observation and `stale` at 90 seconds without a newer observation. Missing and
unselected readings are `null`. CPU and network rates need two observations from
the same boot and valid cumulative counter deltas.

A background worker reads committed observations at startup and every two
seconds. It atomically replaces the generated JSON file in the private state
directory before switching the in-memory snapshot that serves requests. At
startup it rebuilds with the current catalog from SQLite. Keep the SQLite state
directory private. Each successful publication has a higher revision, and a
failed publication can leave gaps in the sequence. The API returns `503` before a
snapshot is ready or when a failed refresh leaves a snapshot beyond its
freshness deadline.

## Realtime overview

Connect to the anonymous SignalR hub at `/hubs/pulse`. `TelemetryUpdated`
delivers one overview snapshot with the same fields as
`GET /api/v1/overview.json`, including its persisted `revision`. The publisher
queues it only after replacing the JSON file and making the snapshot available
to REST. The broadcast worker keeps at most one pending snapshot so a slow
connection cannot hold up publication.

1. Register the event handler, connect to the hub, and fetch the overview JSON.
2. Apply a snapshot from either source only if its revision is newer than the
   last one applied. Repeat the GET whenever the connection recovers.
3. Retry the GET if it returns `503` while the API rebuilds. A delayed or missed
   event can be recovered by fetching the latest snapshot.

Broadcasts are best effort. Revisions can have gaps and keep increasing across
restarts of the same SQLite database. The hub carries the overview only.
Machine, service and history details are fetched from their REST routes.

## Machine details

`GET /api/v1/machines/{alias}.json` accepts an exact public node alias from the
Overview catalog. Unknown, retired and differently cased aliases return `404`.
The response has `schemaVersion`, UTC `generatedAt` and a `machine` object
containing approved hardware, host readouts, filesystems, temperature sensors,
fans and physical disks. Configured readouts with missing values use `null`.
Responses send `Cache-Control: no-store`.

Use the agent's [local inventory command](../agent/README.md) to produce a
[hardware profile](../contracts/hardware-profile.v1.schema.json). Review the
labels and numeric values and keep the approved file outside the checkout in a
private location readable by the API process. The configured profile explicitly
approves the listed machine, CPU and memory fields. To withhold an optional
field, remove it from the file. Review model labels for information that belongs
in private inventory. The API validates the contract, checks field limits and
rejects labels containing paths or recognizable addresses and identifiers.
An invalid configured profile fails startup with an error that omits its content
and path. With no profile configured, hardware is `null`.

Here is an example for the synthetic `demo` node in the Overview setup.
The file path is a placeholder. Leave it unset for live readouts without
hardware inventory.

```sh
export Dashboard__PublicNodes__demo__Machine__HardwareProfilePath=/path/to/reviewed-demo-profile.json
export Dashboard__PublicNodes__demo__Machine__IncludeUptime=true
export Dashboard__PublicNodes__demo__Machine__IncludeLoad=true
export Dashboard__PublicNodes__demo__Machine__Filesystems__system=agent-root
export Dashboard__PublicNodes__demo__Machine__Temperatures__cpu-sensor=agent-cpu
export Dashboard__PublicNodes__demo__Machine__Fans__cpu-fan=agent-fan
export Dashboard__PublicNodes__demo__Machine__Disks__system-drive=agent-disk
```

The main host readouts reuse the Overview metric approvals. The machine-specific
maps use a public alias as the key and the agent's collector alias as the value.
Each map allows up to 16 entries. The disk map selects a physical disk from the
hardware file and its corresponding SMART source. Disk capacity reported by
inventory and filesystem capacity measured by the agent are separate fields.

Host readouts, filesystem space and fast sensors use the host observation time
and become stale at 90 seconds. Approved CPU and network rates start in
`warm` until two compatible samples yield a rate. SMART uses the probe's own
timestamp and becomes stale at three minutes, even when the next host sample
still carries the cached SMART report. The projection compares SMART reports
in the latest 64 committed host envelopes per node. A probe failure is explicit
`unavailable`, and a stale observation retains its timestamp and drops its
numeric values. Disk I/O, inodes and packet drops need corresponding fields in
the agent's wire contract.

The background worker builds machine snapshots from SQLite and replaces a
private manifest atomically. Requests read the in-memory publication. Keep the
state directory private and use the API routes to select published documents.
The route returns `503` for a known alias while its snapshot is preparing or
when a failed refresh leaves live readings past their freshness deadline.

## Service details

`GET /api/v1/services.json` returns `schemaVersion`, UTC `generatedAt` and
`nodes`. Each node groups typed `postgresql`, `redis`, `rabbitmq` and `sqlite`
entries by approved public alias alongside the proxy, CI and Docker groups below. The service catalog is empty by default.

Configure one public alias and one authoritative collector per logical service.
`Node` is the exact public node alias from the Overview catalog. The selected
agent sends one PostgreSQL, Redis or RabbitMQ probe per service kind. SQLite
also requires `Source`, the private collector alias from the agent's SQLite
configuration. The view uses only that reporter's observations for the service.
Duplicate mappings to one collector are rejected. Keep source configuration private.

```sh
export Dashboard__PublicServices__demo-db__Node=demo-edge
export Dashboard__PublicServices__demo-db__Kind=postgresql
export Dashboard__PublicServices__demo-cache__Node=demo-edge
export Dashboard__PublicServices__demo-cache__Kind=redis
export Dashboard__PublicServices__demo-queue__Node=demo-edge
export Dashboard__PublicServices__demo-queue__Kind=rabbitmq
export Dashboard__PublicServices__demo-file__Node=demo-edge
export Dashboard__PublicServices__demo-file__Kind=sqlite
export Dashboard__PublicServices__demo-file__Source=agent-demo-file
```

Each entry shows `alias`, `status`, `observedAt` and `metrics`. Status is
`unknown` before an eligible probe, `unavailable` after a fresh failed probe,
`available` after a fresh successful probe and `stale` when the collector's
freshness window closes. The four database and queue collectors use 90 seconds. Unsupported fields and non-current metrics are `null`. Measured
zero is preserved, including zero queued messages, zero Redis operations per
second and absent SQLite sidecars measured as zero bytes.

- PostgreSQL returns database bytes, connected backends and standby status.
  Transactions per second include commits and rollbacks. Buffer hit percent and
  deadlocks in the window use changes in counters. The rate window requires
  two distinct successful probes on the same agent boot, at most 90 seconds
  apart, with the same recovery role and confirmed statistics reset timestamp.
  A counter decrease invalidates the window. A zero buffer access delta leaves
  the hit percentage unavailable.
- Redis returns memory in bytes, the measured operations per second, clients and
  optional AOF size. A zero memory limit means unlimited, so utilization percent
  is unavailable. Hit percent and evictions use the deltas of the three
  counters across two distinct successful probes on the same agent boot at
  most 90 seconds apart. A decrease or failed probe breaks the window. The v1 wire contract has no Redis server lifetime identifier, so
  a restart or failover that leaves all counters above their previous values
  can escape reset detection.
- RabbitMQ returns current counts for the selected queue. SQLite returns file
  and sidecar sizes in bytes. A SQLite total inconsistent with its parts is
  unavailable.

The worker compares collector timestamps from up to 64 committed host envelopes
per node. Cached copies with the same observation time count as one probe.
It atomically replaces the services JSON in the private state directory before
switching the in-memory publication. The public route sends
`Cache-Control: no-store` and returns `503` until a snapshot is published or
when a failed refresh leaves a snapshot past its freshness deadline.

### Proxy, Gitea Actions and Docker

The same route also returns `nginx`, `nginxAccess`, `tls`, `gitea`,
`giteaActions`, `dockerWorkloads` and `dockerVolumes`. Use
`Dashboard__PublicServices__<public-alias>__Node` to select an approved node and
`Dashboard__PublicServices__<public-alias>__Kind` for each group.

| Kind | Published metrics |
| --- | --- |
| `nginx` | Current connections, total connections and requests, and requests per second from distinct probes on the same boot with non-decreasing counters. The agent reports host boot identity. NGINX process restarts can escape reset detection when counters have already recovered. |
| `nginx-access` | Five-minute request and 5xx counts and the bounded-tail flag. P95 in milliseconds and 5xx percent require a complete, nonempty window. Counts from a truncated tail describe the sampled tail. |
| `tls` | Certificate expiry from the agent's verified TLS probe. |
| `gitea` | Database and cache health from the Gitea probe and its measured response time in milliseconds. |
| `gitea-actions` | Waiting, running and blocked jobs, terminal job counts over the last hour, runner counts and mean duration in seconds when timed jobs exist. |
| `docker-workload` | Runtime state, optional health, restart count and policy, CPU and memory limits and usage, and approved volume attachments with `ro`, `rw` or `mixed` mode. |
| `docker-volume` | Used bytes, available bytes and total capacity of the shared filesystem when measured. `quotaBytes` is `null` with the current agent contract. |

Docker entries also require `Source` set to the corresponding private collector
alias. A workload includes attachments only for volumes approved on the same
public node and translates the attachment names to those volumes' public aliases.
Use `capacityScope: "sharedFilesystem"` to label filesystem totals as shared
storage. Volume sizes stay independent of filesystem capacity and quota.

The proxy and Gitea health probes use a 90-second freshness window. Gitea Actions
and Docker workload snapshots use three minutes, and Docker volume snapshots
use fifteen minutes. An explicit stale result keeps its original timestamp
and exposes `null` metrics. When these snapshot collectors report a failure with
no timestamp, `observedAt` is `null` and the host envelope determines the age of
that failure.

## Health signals

Overview nodes, machine details and individual service readings expose a typed
`signals` array. A signal contains `severity`, `messageKey` and, when the source
supports them, `value`, `unit`, `window` and `threshold`. The UI owns the funny
display copy; the stable keys map to phrases such as `node.ghosted_us` (“This
node ghosted us”), `temperature.getting_hot` (“It’s getting hot in here”),
`disk.getting_crowded` (“Dude, the disk’s getting crowded”), and
`smart.drive_needs_checkup` (“This drive needs a check-up”). Other supported
keys cover busy CPU, crowded RAM, PostgreSQL deadlocks, Redis evictions,
RabbitMQ queue depth, NGINX 5xx percentage, slow or unhealthy Gitea, CI queue
depth, unhealthy Docker workloads, expiring TLS certificates and stale or
failed service probes.

CPU, RAM and temperature signals require distinct, fresh observations that
remain over the threshold for the full sustained window. Re-reading one cached
sample cannot advance the window. A gap of 90 seconds resets the streak. A new
qualifying value below the threshold removes the signal. Disk capacity and
SMART signals describe a current qualified reading. Rate signals are emitted
only when their underlying counter window is valid; truncated NGINX access
windows cannot produce a 5xx signal. A `stale` node or service produces a
ghosted signal, while an `unavailable` service produces a failed-check signal
only after the collector actually reported the failure.
Sustained streak state is intentionally process-local: after an API restart the
receiver waits for a new full window instead of inferring an incident from old
samples.

| Environment variable | Default |
| --- | ---: |
| `Dashboard__Signals__SustainedWindow` | `00:05:00` |
| `Dashboard__Signals__CpuUsagePercent` | `85` |
| `Dashboard__Signals__MemoryUsagePercent` | `90` |
| `Dashboard__Signals__DiskUsagePercent` | `80` |
| `Dashboard__Signals__TemperatureCelsius` | `85` |
| `Dashboard__Signals__NginxServerErrorPercent` | `5` |
| `Dashboard__Signals__GiteaResponseTimeMs` | `1000` |
| `Dashboard__Signals__CiWaitingJobs` | `7` |
| `Dashboard__Signals__RabbitReadyMessages` | `100` |
| `Dashboard__Signals__TlsExpiryDays` | `14` |

Invalid or unsafe thresholds stop startup. The current telemetry contract does
not provide verified backup/restore outcomes, disk I/O latency, packet drops or
volume-growth history, so the API deliberately publishes no signals claiming
those events occurred.

## Public history

`GET /api/v1/history/fleet/{window}.json` serves the fixed `1h`, `24h` and `7d`
windows. Their UTC-aligned resolutions are respectively 2 minutes, 15 minutes
and 1 hour, producing exactly 30, 96 and 168 points. Unknown windows return
`404`. The public handler reads an in-memory, allowlisted snapshot and never
queries SQLite. Responses use `Cache-Control: no-store`.

Each point contains aggregate CPU and memory only when approved measurements
exist. CPU is weighted by the reviewed logical CPU count in each participating
machine's hardware profile. Memory is summed from measured used and total bytes.
The `nodes` pulse lists only public aliases and reports `available` for an
observed bucket or `missing` for a gap. A missing bucket is not converted to a
zero. Counter decreases, boot changes and sample gaps over 90 seconds break CPU
rate calculation rather than creating a misleading value.

The history worker rebuilds the bounded series from committed SQLite samples at
startup and every five minutes. It orders observations deterministically and
therefore incorporates valid late arrivals on the next rebuild. Fleet files are
atomically replaced before the in-memory response is switched. A failed refresh
is retried at the next interval; the route returns `503` once its last successful
snapshot is more than ten minutes old.

The SQLite query ranks observations inside each requested bucket and reads at
most the latest two. That bound protects the homelab's spinning disks while
retaining the adjacent pair needed for a valid rate and deterministic latest
reading. It does not load the seven-day raw telemetry set into memory.

Machine details include the same bounded 1-hour history for that approved alias.
The larger fleet windows stay on the dedicated history routes to keep each
machine document small. The public history never includes agent IDs, boot IDs,
sample IDs, raw counters or source selectors.
