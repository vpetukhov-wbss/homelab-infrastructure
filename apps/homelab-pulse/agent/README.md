# HomeLab Pulse Agent

A Linux agent that measures host telemetry and sends it to the private HomeLab Pulse ingest API. This first slice targets Python 3.10+ with the standard library. It reads CPU, memory, swap, load and uptime from Linux, filesystem capacity from `statvfs`, and optional network and temperature counters from `sysfs`.

## Run on a Linux development machine

```bash
cd apps/homelab-pulse/agent
PYTHONPATH=src python3 -m pulse_agent --config config.example.ini sample
PYTHONPATH=src python3 -m unittest discover -s tests -v
```

The `sample` command prints one private payload to stdout without requiring a token or making a network request. Filesystem, network and temperature results use aliases from the configuration supplied by the operator. Use synthetic measurements in public demo artifacts.

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
- `pulse_agent_filesystems`, `pulse_agent_interfaces` and `pulse_agent_temperatures` map approved aliases to host resources.

The role uses the host Python interpreter, stores samples in a systemd-managed state directory and loads the bearer token through `LoadCredential=`. The host cycle runs every 15 seconds after the WireGuard and overlay guard units are active. Add nodes to the playbook inventory after the .NET receiver accepts the contract.

## Scope

The scaffold covers the fast host cycle, queue, HTTP transport and deployment templates. SMART, Docker and database, broker, web server and CI probes are follow-up collectors. The .NET receiver owns history, retention, public projection and SignalR.
