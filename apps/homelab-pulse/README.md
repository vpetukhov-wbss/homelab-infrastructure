# HomeLab Pulse

My own monitoring for the homelab. A small agent on every Linux machine measures the
host and the services on it, and sends the readings over WireGuard to a private .NET
receiver. The receiver keeps the history and drives a live dashboard.

> **Status, 2026-11-22.** The agent, its contracts and its Ansible role are in this
> repository, and the agent's unit tests pass. **Nothing is deployed yet:** no machine
> is in the `pulse_agents` inventory group. Machines join once the .NET receiver,
> developed outside this repository, accepts the contract.

| Part | Where | State |
|---|---|---|
| Agent: Python 3.10+, standard library only | [`agent/`](agent/) | written, unit-tested |
| Contracts: JSON Schema | [`contracts/`](contracts/) | version 1 |
| Deployment: Ansible role and an opt-in playbook | [`pulse_agent`](../../ansible/roles/pulse_agent/), [`pulse-agent.yml`](../../ansible/playbooks/pulse-agent.yml) | not applied to any machine |
| Receiver: history, retention, dashboard (.NET, SignalR) | outside this repository | in development |

## How it fits together

~~~mermaid
flowchart TB
    subgraph host ["one Linux machine"]
        t2(["pulse-smart.timer<br/>every 60 s"]) --> smart["pulse-smart.service<br/>root · no network<br/>only the listed disks"]
        smart -- "smartctl -j -H -A" --> disks[("approved disks")]
        smart -- "normalized JSON<br/>atomic write" --> snap[("/run/pulse-smart")]
        t1(["pulse-agent.timer<br/>every 15 s"]) --> agent["pulse-agent.service<br/>user pulse-agent · starts only while<br/>wg1 and the overlay guard are active"]
        kernel[("/proc · /sys · statvfs")] --> agent
        snap --> agent
        agent -- "one file per sample" --> outbox[("outbox<br/>/var/lib/pulse-agent")]
    end
    optional["optional probes, enabled per machine<br/>NGINX stub_status · SQLite file sizes<br/>Redis INFO, own ACL user<br/>PostgreSQL statistics, own read-only role<br/>RabbitMQ queue depth, own management user"] --> agent
    outbox -- "POST /internal/v1/telemetry<br/>bearer token · over wg1" --> api["receiver (.NET), outside this repository<br/>history · retention<br/>live dashboard (SignalR)"]
~~~

Each machine runs two separate pieces:

- the **agent** is networked and unprivileged;
- the **SMART producer** may read the disks, but has no network and cannot read the
  agent's credentials.

They share only a directory of normalized snapshots.

## One collection cycle

~~~mermaid
sequenceDiagram
    autonumber
    participant T as systemd timer
    participant A as pulse-agent tick
    participant C as collectors
    participant O as outbox
    participant R as receiver (.NET)
    T->>A: start, every 15 s
    A->>O: take the sender lock<br/>(non-blocking)
    opt the previous tick is still running
        A-->>T: exit 0, skip this cycle
    end
    A->>C: collect host + probes
    C-->>A: values, per probe:<br/>available, unavailable or stale
    A->>O: write the sample file<br/>(temp, fsync, rename)
    Note over A,O: if collection failed,<br/>the backlog is still delivered
    A->>O: prune: over 512 files<br/>or older than 24 h
    loop the newest sample, then up to 3 of the oldest
        A->>R: POST sample with the bearer token<br/>2 s timeout, no proxy, no redirects
        alt 204, stored or already accepted
            R-->>A: 204
            A->>O: delete the file
        else 400, 413 or 422, an invalid sample
            R-->>A: rejected
            A->>O: log the sample ID, delete the file
        else any other answer, or none
            R-->>A: error or timeout
            A-->>T: exit 1, queue kept for the next cycle
        end
    end
~~~

## What happens to a queued sample

~~~mermaid
flowchart TD
    start(["tick starts"]) --> lock{"sender lock free?"}
    lock -- no --> skip(["exit 0: another tick is running"])
    lock -- yes --> collect["collect one host sample"]
    collect -- ok --> write["write the sample file<br/>temp file → fsync → rename"]
    collect -- fails --> flag["note the failure for the exit code"]
    write --> prune
    flag --> prune["prune: drop files beyond 512<br/>or older than 24 h"]
    prune --> pick["order: newest first,<br/>then up to 3 of the oldest"]
    pick --> next{"another file?"}
    next -- no --> done(["exit 0, or 1 after a failed collection"])
    next -- yes --> size{"larger than 64 KiB?"}
    size -- yes --> drop["delete it"] --> next
    size -- no --> send["POST it to the receiver"]
    send --> answer{"answer"}
    answer -- "204" --> ack["delete it"] --> next
    answer -- "400 · 413 · 422" --> rejected["log the sample ID, delete it"] --> next
    answer -- "anything else, or a timeout" --> keep(["exit 1: the samples stay queued"])
~~~

Delivery is at least once: a sample can arrive twice when an answer is lost on the way
back. The receiver deduplicates by the authenticated agent and the sample ID, and orders
readings by their observation time. An outage longer than a day, or more than 512 queued
samples, loses the oldest readings rather than filling the disk.

## SMART readings

~~~mermaid
sequenceDiagram
    autonumber
    participant T as pulse-smart.timer
    participant P as SMART producer
    participant D as smartctl
    participant F as /run/pulse-smart
    participant A as pulse-agent
    T->>P: every 60 s, as root,<br/>without network
    loop each approved disk
        P->>D: smartctl -j -H -A<br/>5 s limit, SATA in standby not woken
        D-->>P: JSON report
        Note right of P: keep health, temperature,<br/>wear, error counters<br/>drop serials, paths,<br/>raw attributes
        P->>F: replace alias.json<br/>atomically, mode 0640
    end
    Note over A: every tick
    A->>F: read the snapshot
    alt younger than 3 minutes
        F-->>A: values, original<br/>observation time
    else older
        F-->>A: status stale, no values
    else missing, malformed or failed
        F-->>A: status unavailable
    end
~~~

A snapshot dated in the future also counts as unavailable.

## Redis, PostgreSQL and RabbitMQ probes

~~~mermaid
sequenceDiagram
    autonumber
    participant A as pulse-agent tick
    participant K as probe cache in the state directory
    participant S as Redis, PostgreSQL or RabbitMQ
    A->>K: a reading of the same target younger than 30 s?
    alt yes
        K-->>A: reuse it, with its original observation time
    else no
        A->>S: Redis: INFO as its own ACL user<br/>PostgreSQL: statistics in a read-only transaction<br/>RabbitMQ: one queue through the management API<br/>each with a 1 s limit
        alt it answers
            S-->>A: metrics
        else an error or a timeout
            S-->>A: nothing, reported as unavailable
        end
        A->>K: save the new reading
    end
~~~

Each probe expects its own account:

- a Redis ACL user that may run `INFO` and touch no keys;
- a PostgreSQL login with `CONNECT` on one database;
- a RabbitMQ user with the `management` tag and no permissions on the virtual host
  (`^$`), which reads one queue.

The agent refuses the `postgres` superuser and any address that is not loopback or
private (RFC 1918 or IPv6 unique-local). The accounts themselves belong to the Redis,
PostgreSQL and RabbitMQ roles, which do not create them yet.

## Hardware inventory

~~~mermaid
flowchart TB
    owner(["the owner, on the machine"]) --> inventory["pulse_agent inventory<br/>no token, nothing is sent"]
    dmi["sudo dmidecode --type 17<br/>optional: RAM modules"] -. "stdin" .-> inventory
    disks["disk aliases from agent.ini<br/>optional"] -.-> inventory
    inventory --> profile["hardware-profile.v1 JSON on stdout<br/>no serials, UUIDs, asset tags or hostnames"]
    profile --> review{"the owner reviews it"}
    review --> catalog["dashboard hardware catalog"]
~~~

## Deployment

~~~mermaid
flowchart TD
    run["ansible-playbook playbooks/pulse-agent.yml<br/>hosts: pulse_agents (empty today)"] --> asserts{"a private ingest URL and<br/>a per-host token in the vault?"}
    asserts -- no --> stop(["fails before changing anything"])
    asserts -- yes --> account["system user pulse-agent<br/>agent source in /opt/pulse-agent"]
    account --> token["ingest token in a root-only file,<br/>handed over by systemd LoadCredential"]
    token --> smartq{"SMART disks listed?"}
    smartq -- yes --> smartyes["requires smartctl · writes the device list<br/>installs and starts pulse-smart.timer"]
    smartq -- no --> smartno["stops and disables an earlier SMART timer"]
    smartyes --> creds
    smartno --> creds["Redis password and PostgreSQL pgpass<br/>only for the probes that are enabled"]
    creds --> config["renders agent.ini · installs the units<br/>starts pulse-agent.timer"]
    config --> verify["reads back systemctl is-active pulse-agent.timer"]
~~~

## Security boundaries

- **Unprivileged and fenced.** The agent runs as the `pulse-agent` system user under
  `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp` and `NoNewPrivileges`, and starts
  only while `wg1` and the overlay guard are active.
- **Secrets stay out of the command line and the environment.** The ingest token, the
  Redis password and the PostgreSQL password file are root-only files (mode 0600) that
  systemd hands to the service as credentials.
- **Only the SMART producer is privileged, and it has no network.** It runs as root
  with `PrivateNetwork`, may open only the listed disks, read-only, keeps
  `CAP_SYS_RAWIO` (plus `CAP_SYS_ADMIN` for NVMe), and cannot see the agent's
  credentials or queue.
- **Only private targets.** Probe addresses must be loopback or private literals. HTTP
  requests bypass proxies and refuse redirects.
- **Nothing identifying leaves the machine.** Samples carry the owner's aliases, not
  device paths, serials or hostnames, and are capped at 64 KiB.

## More

- Configuration and every collector: [agent/README.md](agent/README.md).
- Request format: [contracts/telemetry-sample.v1.schema.json](contracts/telemetry-sample.v1.schema.json).
- Hardware profile: [contracts/hardware-profile.v1.schema.json](contracts/hardware-profile.v1.schema.json).
