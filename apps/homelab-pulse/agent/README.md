# HomeLab Pulse Agent

A Linux agent that measures host telemetry and sends it to the private HomeLab Pulse ingest API. This first slice targets Python 3.10+ with the standard library. It reads CPU, memory, swap, load and uptime from Linux, filesystem capacity from `statvfs`, and optional network, temperature and fan speed readings from `sysfs`.

## Run on a Linux development machine

```bash
cd apps/homelab-pulse/agent
PYTHONPATH=src python3 -m pulse_agent --config config.example.ini sample
PYTHONPATH=src python3 -m pulse_agent inventory
PYTHONPATH=src python3 -m unittest discover -s tests -v
```

The `sample` command prints one private payload to stdout without requiring a token or making a network request. Filesystem, network, temperature and fan results use aliases from the configuration supplied by the operator. Use synthetic measurements in public demo artifacts.

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

The role uses the host Python interpreter, stores samples in a systemd-managed state directory and loads the bearer token through `LoadCredential=`. The host cycle runs every 15 seconds after the WireGuard and overlay guard units are active. Add nodes to the playbook inventory after the .NET receiver accepts the contract.

## Scope

The agent covers the fast host cycle, managed SMART polling, queue, HTTP transport and deployment templates. Docker, database, broker, web server and CI probes are follow-up work. The .NET receiver owns history, retention, public projection and SignalR.
