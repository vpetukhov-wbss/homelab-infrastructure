# HomeLab Pulse Agent

A Linux agent that measures host telemetry and sends it to the private HomeLab Pulse ingest API. This first slice targets Python 3.10+ with the standard library. It reads CPU, memory, swap, load and uptime from Linux, filesystem capacity from `statvfs`, and optional network, temperature and fan speed readings from `sysfs`.

## Run on a Linux development machine

```bash
cd apps/homelab-pulse/agent
PYTHONPATH=src python3 -m pulse_agent --config config.example.ini sample
PYTHONPATH=src python3 -m pulse_agent inventory
PYTHONPATH=src python3 -m unittest discover -s tests -v
```

The `sample` command prints one private payload to stdout without contacting ingest or requiring its token. It runs any configured service probes. Filesystem, network, temperature and fan results use aliases from the configuration supplied by the operator. Use synthetic measurements in public demo artifacts.

Enable fan monitoring with the optional `[fans]` section. Map each approved alias to an absolute Linux hwmon `fanN_input` path readable by the agent. The normal host cycle reports `fanSpeedsRpm` in RPM, including readings of zero. Failed or invalid readings are omitted for that cycle, and the agent logs only the alias. With no configured fans, the field is omitted.

The `inventory` command prints a [hardware profile](../contracts/hardware-profile.v1.schema.json) for review before adding it to the dashboard catalog. It reads available machine and CPU models, architecture, logical CPUs, reported physical cores and OS-visible RAM. The profile excludes serial numbers, system UUIDs, asset tags and hostnames. Values from DMI and CPU topology may be absent on virtual machines.

To include installed RAM modules, pipe a local SMBIOS Type 17 report into the inventory command. Only the administrator-run firmware reader may need elevated privileges.

```bash
sudo dmidecode --type 17 | PYTHONPATH=src python3 -m pulse_agent inventory --smbios-memory
```

Each populated module with a known capacity contributes its size in bytes and its reported RAM type and speeds, when available. The profile distinguishes rated and configured speeds in MT/s. Legacy MHz and unknown speed values are omitted. Empty slots, serial numbers, asset tags, part numbers and slot identifiers are excluded from the export. Hosts without usable SMBIOS records still report OS-visible RAM. The periodic agent runs as its existing unprivileged account.

To include disk profiles, pass `--config` and map approved aliases to whole device names under `/sys/block` in the optional `[disks]` section. For example, `system = nvme0n1` selects the OS-visible NVMe namespace under the alias `system`. Kernel device names can change, so review the mapping after replacing or reordering drives. The command runs without elevated privileges or an ingest token.

```bash
PYTHONPATH=src python3 -m pulse_agent --config private/agent.ini inventory
```

Disk profiles report the kernel-visible capacity in bytes, model and rotational flag when available. A missing or unsupported device returns `{"status":"unavailable"}` under its alias. Device paths and serial numbers are excluded. Virtual machines report the devices visible to the guest. The result is written locally to stdout for review.

## SMART snapshots

The host cycle can attach `diskSmart` readings from the optional `[smart_snapshots]` alias-to-file mappings. The agent reads sanitized snapshots and keeps its existing unprivileged account. For a local check, run the device reader on one approved drive and pipe its JSON directly into the normalizer.

```bash
sudo smartctl -j -H -A /dev/nvme0n1 | PYTHONPATH=src python3 -m pulse_agent smart-snapshot
```

For managed polling, set `pulse_agent_smart_devices` in the private Ansible inventory. Use the basename of each approved SATA disk or NVMe namespace. For example:

```yaml
pulse_agent_smart_devices:
  archive: sda
  system: nvme0n1
```

The host needs `smartctl` from `smartmontools`, which the host hardening role provides. Ansible enables a separate 60-second systemd timer when the mapping is nonempty. Its service reads only the listed device nodes, runs without network access, and writes group-readable normalized snapshots to `/run/pulse-smart` using atomic file replacement. SATA probes skip standby disks. The service gives each device five seconds to respond. Clear the mapping to stop managed polling. Review kernel device names after hardware changes.

`pulse_agent_smart_snapshots` still accepts files from an external local producer. Keep them readable by `pulse-agent` and writable only by their producer. When both settings use an alias, the managed snapshot takes precedence.

The normalizer preserves the source observation time and selected health, temperature, NVMe wear and error counters, or recognized ATA sector counters. It omits serial numbers, paths, command lines and raw attributes. Missing, malformed, failed or future-dated snapshots report `unavailable`. Readings older than three minutes retain their observation time with `stale` status and omit the metric values.

## NGINX

Set `nginx_status_url` under `[agent]` or `pulse_agent_nginx_status_url` in private Ansible inventory to an existing private endpoint that serves NGINX `stub_status`. Use an HTTP or HTTPS URL with a loopback, RFC 1918, or IPv6 unique-local address literal. The agent sends a GET without credentials, proxies, or redirects. It does not enable a listener or change the NGINX configuration.

The host cycle records active, reading, writing and waiting connections along with cumulative accepted connections, handled connections and requests. Each reading includes its own UTC observation time. Connection errors, timeouts, unexpected HTTP responses and malformed bodies report `unavailable` while host metrics continue. The socket timeout is one second and the response is limited to 1 KiB. With an empty URL, the agent omits this collector.

## SQLite storage

Map each approved database alias to its absolute path in `[sqlite_databases]`, or set `pulse_agent_sqlite_databases` in private Ansible inventory. The normal host cycle records the logical size of the database, WAL, shared-memory index and rollback journal in bytes, along with their sum and a UTC timestamp. Absent sidecars count as zero. Since SQLite can create or delete these files during a write or checkpoint, sizes are a point-in-time estimate. Missing or inaccessible databases and non-regular files report `unavailable` independently of other metrics.

The collector uses filesystem metadata only. It opens no SQLite connections, performs no queries and exports no paths or database contents. Use paths that the `pulse-agent` service can traverse. The app-host data directory lives under a home directory, which systemd hides from the agent by default. When enabling the collector for that directory, use a local `pulse-agent.service` drop-in that exposes only the required SQLite directory read-only. The following paths are examples:

```ini
[Service]
ProtectHome=tmpfs
BindReadOnlyPaths=/home/example/stacks/apphost/sqlite
```

Grant the service account directory traversal only where needed. A backup file can also be measured by configuring its path and using an alias that identifies it as a backup.

## Redis

The optional `[redis]` section targets a single Redis 7 server per agent. Set `host` to a loopback or private IP literal on the trusted WireGuard network, with optional `port` and `username` settings. Use a dedicated ACL account with `+info` and no key or channel access. The probe uses a plain Redis connection, so use loopback or the encrypted overlay. The role leaves ACL provisioning and Redis listeners to the Redis operator.

Ansible uses `pulse_agent_redis_host`, `pulse_agent_redis_port` and `pulse_agent_redis_username`. Set `vault_pulse_agent_redis_password` in the private vault. The rendered service gets a separate `redis-password` credential. For a local CLI run, set `password_file` in `[redis]` to a readable, absolute credential file path.

The agent reports used memory, configured memory limit, instantaneous operations per second, connected clients, cache hit and miss totals, evictions and AOF size when AOF is enabled. A zero memory limit indicates Redis reported no configured cap. Compute hit ratio from changes in hit and miss counters within the same server life cycle. Each periodic probe uses a one-second deadline and Redis 7's multi-section `INFO` request. The host cycle reuses a reading for 30 seconds with its original observation time. Failures report `unavailable` while other metrics continue. The CLI `sample` command always takes a fresh reading without writing the cache.

## PostgreSQL

Enable `[postgresql]` for one approved database per agent. Set `host` to a loopback or private IP literal on the trusted WireGuard network. Configure `port`, `database` and a dedicated `username`. Grant the monitoring login CONNECT on the selected database. The standard statistics query requires no privileges on application tables. The client requests read-only transactions and prefers TLS if the server offers it. Use loopback or WireGuard for this probe.

In Ansible, set `pulse_agent_postgres_host`, `pulse_agent_postgres_port`, `pulse_agent_postgres_database` and `pulse_agent_postgres_username`. Store a libpq password-file entry for the exact host, port, database and user in `vault_pulse_agent_postgres_pgpass`. The role installs `postgresql-client` and supplies the file through systemd credentials. For manual Linux testing with `psql` installed, set `pgpass_file` to an absolute path with mode 0600.

The agent reports database size, connected backends, recovery state and cumulative commit, rollback, buffer hit, block read and deadlock counters. The probe itself counts as a connection. The collector includes the statistics reset time when PostgreSQL provides one. Use counter deltas and start a new series after a reset. The host cycle reuses an observation for 30 seconds with its original timestamp. The database statement has a one-second timeout and the client process has a two-second limit. Failed probes report `unavailable` while other metrics continue. A manual `sample` command takes a fresh reading.

## RabbitMQ

Enable `[rabbitmq]` to monitor one approved queue per agent on the existing RabbitMQ 3.13 management listener. Set `host` to the management listener's private IP literal, `port` to its HTTP port (default 15672), `vhost` to the exact virtual host name and `queue` to the exact queue name. Use loopback or the encrypted WireGuard overlay because the management request uses HTTP Basic authentication.

Set `username` to a dedicated account with the `management` tag. Grant it access to the selected virtual host with configure, write and read permission patterns all set to `^$`. The [management tag](https://www.rabbitmq.com/docs/3.13/management#access-and-permissions) allows visibility into other queues in that virtual host, so the probe requests only the approved queue using `GET /api/queues/{vhost}/{name}`. It selects total messages, messages ready to deliver, delivered messages awaiting acknowledgement and consumer count. The telemetry contains those counts and the UTC observation time.

For Ansible, set `pulse_agent_rabbitmq_host`, `pulse_agent_rabbitmq_port`, `pulse_agent_rabbitmq_vhost`, `pulse_agent_rabbitmq_queue` and `pulse_agent_rabbitmq_username` in private inventory. Put the password in `vault_pulse_agent_rabbitmq_password` for the `rabbitmq-password` systemd credential. For a local CLI check, set `password_file` in `[rabbitmq]` to an absolute path readable by the caller. Clear the host to disable the probe.

Each probe uses a one-second socket timeout and reads at most 64 KiB of management JSON plus one byte to detect oversized responses. An unreachable broker, failed authentication, missing queue or missing counters yield `unavailable`. Zero indicates a reported zero. The periodic host cycle reuses a reading for 30 seconds with its original timestamp. The `sample` command takes a fresh reading.

## Gitea

Set `gitea_health_url` under `[agent]` or `pulse_agent_gitea_health_url` in private Ansible inventory. Use the existing Gitea 1.22 `/api/healthz` endpoint on loopback or a private IP literal. The probe uses GET without credentials, proxies or redirects. It reports database and cache health and the HTTP response time in milliseconds, excluding the response text and endpoint details.

Gitea can respond with HTTP 424 and still provide valid readings that identify an unhealthy dependency. A connection failure or a response without both Gitea checks is `unavailable`. The probe uses a one-second socket timeout and accepts up to 8 KiB of JSON. The periodic host cycle reuses an observation for 30 seconds, keeping its original UTC timestamp. The `sample` command probes directly. Gitea Actions queue, run durations and runner status are separate measurements.

## Docker workloads

Set `pulse_agent_docker_containers` in private Ansible inventory to a mapping of up to eight approved aliases to exact Docker container names. For example, map `worker` to `demo-worker`. The role uses the Docker CLI and curl installed by the Docker role. It schedules `pulse-docker.timer` every 60 seconds. Each inspect has a two-second timeout. Each non-streaming stats transfer has a three-second deadline, with 50 seconds for the full service cycle.

Only the separate `pulse-docker.service` gets the Docker supplementary group while running. Treat that group as privileged. The service uses a fixed local Unix socket for container inspect and a stats GET for each selected running container. The main agent has the socket hidden and reads only group-readable snapshots. Container names and IDs, raw Docker responses, environment variables and health messages stay out of the snapshots and the telemetry.

`dockerWorkloads` reports container state, health when Docker reports one, the container's restart counter and policy, and configured CPU and memory limits where present. The restart counter belongs to the current container instance. Limits describe configuration.

The optional `usage` object carries measured CPU usage and memory for the snapshot's UTC observation time. One core means 100% of a logical CPU. CPU uses Docker's current and previous counters and the host CPU count. Missing or reset counters omit the CPU field. For memory, the collector subtracts inactive file cache when available and valid, using `total_inactive_file` on cgroup v1 and `inactive_file` on cgroup v2. It uses raw cgroup usage when the cache counter is unavailable. A failed stats probe marks `usage` unavailable while retaining container metadata. Stopped containers omit `usage`.

Set `pulse_agent_docker_volumes` in private inventory to up to 16 public-safe aliases mapped to distinct exact named volumes. The existing Docker probe adds `volumeAttachments` to each inspected workload. It reports `ro`, `rw`, or `mixed` when one workload has both modes for an approved volume. An empty map means the workload has no approved volume attachments. A stopped container can still have configured attachments. Original names, mount paths and bind mounts stay out of telemetry. Volume size and backing filesystem capacity are separate measurements.

An unsuccessful inspect records `unavailable`. A snapshot over three minutes old reports `stale` with its original observation time. The `sample` command can read normalized files via `[docker_snapshots]`, mapping each alias to an absolute path. Clear the inventory mapping to disable managed polling.

## Delivery

The versioned request format lives in [the shared contract](../contracts/telemetry-sample.v1.schema.json). A `tick` persists a sample in the local outbox and sends it to `POST /internal/v1/telemetry` over the private network. The API associates the bearer credential with the node and returns `204` for a stored or already accepted sample.

The sender tries the newest sample first, then replays a bounded batch of older samples. A transient failure leaves the original payload and sample ID in the queue. The API should deduplicate by authenticated agent and sample ID and order current readings by observation time. Invalid payload responses (400, 413, 422) are logged by sample ID and dropped. Authentication errors retain samples for the next run. Age and count limits bound the outbox during prolonged outages.

A direct test of `tick` needs a private config and a local credential file.

```bash
PYTHONPATH=src python3 -m pulse_agent --config private/agent.ini --token-file private/ingest-token tick
```

## systemd and Ansible

The repository contains an opt-in `ansible/playbooks/pulse-agent.yml` that targets a `pulse_agents` inventory group. Configure the following in the private inventory.

- `pulse_agent_ingest_url` points at the private HTTP or HTTPS endpoint reachable through WireGuard.
- `vault_pulse_agent_ingest_token` is unique for each host and comes from private encrypted inventory.
- `pulse_agent_filesystems`, `pulse_agent_interfaces`, `pulse_agent_temperatures` and `pulse_agent_fans` map approved aliases to host resources.
- `pulse_agent_disks` maps approved inventory aliases to block device names on each host. On a deployed node, pass `--config /etc/pulse-agent/agent.ini` to `inventory` to use the rendered mapping.
- `pulse_agent_smart_devices` enables managed SMART polling for the listed disks. `pulse_agent_smart_snapshots` maps additional external SMART snapshots.
- `pulse_agent_docker_containers` enables the separate Docker workload producer.
- `pulse_agent_nginx_status_url` enables probes against an existing private NGINX status endpoint.
- `pulse_agent_gitea_health_url` enables probes against the existing private Gitea health endpoint.
- `pulse_agent_sqlite_databases` maps approved aliases to SQLite files visible inside the service.
- `pulse_agent_redis_host` enables Redis monitoring using the dedicated service credential.
- `pulse_agent_postgres_host` enables PostgreSQL monitoring using the dedicated libpq password file.
- `pulse_agent_rabbitmq_host` enables queue monitoring using the dedicated RabbitMQ management credential.

The role uses the host Python interpreter, stores samples in a systemd-managed state directory and loads the bearer token through `LoadCredential=`. The host cycle runs every 15 seconds after the WireGuard and overlay guard units are active. Add nodes to the playbook inventory after the .NET receiver accepts the contract.

## Scope

The agent covers the fast host cycle, managed SMART polling, Docker workload metrics and attachments, NGINX status, SQLite storage, Redis INFO, PostgreSQL statistics, RabbitMQ queue depth, Gitea health, the local outbox, HTTP transport and deployment templates. NGINX access-log and TLS metrics, Docker volume capacity, and Gitea Actions probes are follow-up work. The .NET receiver owns history, retention, public projection and SignalR.
