# HomeLab Infrastructure

![Ubuntu Server](https://img.shields.io/badge/Ubuntu%20Server-22.04%20%7C%2024.04%20%7C%2026.04-E95420?logo=ubuntu&logoColor=white)
![Ansible](https://img.shields.io/badge/Configuration-Ansible-EE0000?logo=ansible&logoColor=white)
![Docker](https://img.shields.io/badge/Runtime-Docker%20Compose-2496ED?logo=docker&logoColor=white)
![WireGuard](https://img.shields.io/badge/Overlay-WireGuard-88171A?logo=wireguard&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16%20primary%20%2B%20standby-4169E1?logo=postgresql&logoColor=white)
![Redis](https://img.shields.io/badge/Redis-7.4%20%2B%20Sentinel-DC382D?logo=redis&logoColor=white)
![Gitea](https://img.shields.io/badge/Git%20%2B%20CI-Gitea%20Actions-609926?logo=gitea&logoColor=white)
[![HomeLab Pulse](https://img.shields.io/badge/Monitoring-HomeLab%20Pulse%20%28in%20development%29-3776AB?logo=python&logoColor=white)](apps/homelab-pulse/README.md)
![License](https://img.shields.io/badge/License-MIT-blue)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-D97757?logo=claude&logoColor=white)](docs/claude-code.md)

A self-hosted platform on four second-hand machines, configured entirely with Ansible
and run by one person. The repository is also an honest record of how it was built,
including what went wrong.

## The goal

One owner, four machines, and:

- **the services my applications need**: replicated PostgreSQL and Redis, RabbitMQ,
  SQLite and SFTP today; **MongoDB** and an AWS emulator (**LocalStack**) next;
- **self-hosted Git with CI**, application hosting behind a single edge, and a password
  manager of my own (**Vaultwarden**);
- **my own monitoring**, [HomeLab Pulse](apps/homelab-pulse/README.md): an agent on
  every machine that feeds a live dashboard (in development);
- **reachable from anywhere by me, over WireGuard**, and by nobody else;
- **encrypted backups with a tested restore**;
- **every machine rebuildable** from a USB stick and one password, including the
  controller, which lives on node4 together with Claude Code.

[docs/roadmap.md](docs/roadmap.md) breaks this into milestones.

## The machines, which set every limit

| | node1 | node2 | node3 | node4 |
|---|---|---|---|---|
| Hardware | Lenovo desktop, Pentium G620 | Lenovo desktop, Pentium G630 | Dell OptiPlex 755, Core 2 Duo | ThinkPad L470, i5-6200U |
| RAM / disk | 3.7 GB / 250 GB HDD | 3.7 GB / 250 GB HDD | 3.7 GB / 500 GB HDD | 7.3 GB / 256 GB SSD |
| Network | wired | wired | wired | Wi-Fi |
| Runs | **Edge**: Caddy, Vault, SFTP, RabbitMQ, Sentinel, backups | PostgreSQL **primary**, Redis, Sentinel | PostgreSQL standby, Redis, Sentinel, app host, Gitea + CI runner, SFTP mirror, backup copy | **Controller** (Ansible, Claude Code), headless since 2026-11-22. Next: joins wg1, then MongoDB, Vaultwarden, LocalStack and monitoring |

The CPUs have two cores and the disks spin. **Disk I/O is the ceiling**, and containers
do not change that. Memory is not the limit yet. Measured on the live machines on
2026-11-22, node4 on 2026-11-23 after its reinstall (grey: installed, blue: in use):

~~~mermaid
---
config:
  themeVariables:
    xyChart:
      plotColorPalette: "#c9d1d9, #1f6feb"
---
xychart-beta
    title "Memory in use vs installed (GiB)"
    x-axis ["node1", "node2", "node3", "node4"]
    y-axis "GiB" 0 --> 8
    bar [3.65, 3.66, 3.68, 7.13]
    bar [0.74, 0.69, 0.78, 0.90]
~~~

~~~mermaid
---
config:
  themeVariables:
    xyChart:
      plotColorPalette: "#c9d1d9, #1f6feb"
---
xychart-beta
    title "Root filesystem used vs size (GB)"
    x-axis ["node1", "node2", "node3", "node4"]
    y-axis "GB" 0 --> 500
    bar [243, 243, 490, 248]
    bar [16, 9, 17, 9]
~~~

node4 was reinstalled headless on 2026-11-22 and runs nothing but the controller yet, so
its bars show an idle server. Why each service sits where it does:
[docs/PROJECT-CONTEXT.md §3](docs/PROJECT-CONTEXT.md#3-role-assignment-and-why-each-one-landed-where-it-did).

## How it fits together

~~~mermaid
flowchart TB
    subgraph mesh ["wg1 · WireGuard full mesh · 10.44.0.0/24"]
        direction LR
        n1["node1 · edge · 10.44.0.1<br/>Caddy · Vault · SFTP · RabbitMQ<br/>Sentinel · nightly backup job"]
        n2["node2 · data · 10.44.0.2<br/>PostgreSQL primary<br/>Redis · Sentinel"]
        n3["node3 · data + apps · 10.44.0.3<br/>PostgreSQL standby · Redis · Sentinel<br/>nginx + SQLite apps · Gitea + runner<br/>SFTP mirror · backup copy"]
        n1 <--> n2
        n2 <--> n3
        n3 <--> n1
    end
    n4["node4 · controller · 10.44.0.4<br/>headless laptop on Wi-Fi<br/>Ansible · Claude Code"]
    n4 <-- "wg1, direct to each server" --> mesh
    n4 -- "Ansible: SSH over the LAN, pinned host keys" --> mesh
~~~

- **The mesh has no hub.** Every machine peers directly with the other three, so any one
  of them can fail without cutting the others apart.
- **Every database, broker, Vault and application port binds to the overlay.** The LAN
  sees only SSH and Caddy.
- **Roles move.** Redis Sentinel moves the master and a promotion moves the PostgreSQL
  primary, so scripts discover them at run time instead of trusting host names.

Full topology and service catalog:
[docs/architecture/overview.md](docs/architecture/overview.md).

## Who can reach what

~~~mermaid
flowchart LR
    internet(["Internet"])
    lanhost(["any device on the LAN"])
    peer(["a wg1 peer"])

    internet -- "TCP 2345<br/>the only router forward today" --> laptop["node4 SSH :2345<br/>keys only · Fail2Ban"]
    internet -. "planned: four UDP ports for WireGuard<br/>then 2345 is closed" .-> peer

    lanhost -- "SSH, LAN subnet only" --> nodessh["node1 :2222<br/>node2 :22 · node3 :22<br/>keys only · Fail2Ban"]
    lanhost -- "HTTP / HTTPS" --> caddy["node1 Caddy<br/>:80 / :443<br/>no router forward yet"]
    lanhost -- "adds a route<br/>to 10.44.0.x" --x guard{{"overlay guard<br/>raw table: drops overlay<br/>traffic not from wg1"}}
    peer --> guard
    caddy -- "reverse proxy" --> guard
    guard --> svc["overlay-only services<br/>PostgreSQL · Redis<br/>Sentinel · RabbitMQ<br/>Vault · SFTP<br/>Gitea · app host"]
~~~

Docker's port publishing bypasses ufw, so the bind address alone did not keep overlay
services off the LAN: on 2026-11-21 a LAN machine with one extra route reached **14 of
14** of them. The overlay guard drops that traffic before Docker's NAT sees it. The same
test afterwards: **14 of 14 blocked**. Details and the remaining gaps:
[docs/security/posture.md](docs/security/posture.md).

## How data is protected

~~~mermaid
flowchart TB
    subgraph live ["Live copies: they survive a dead disk, not a mistake"]
        direction LR
        pgp[("PostgreSQL primary<br/>node2")] -- "async streaming<br/>replication" --> pgs[("PostgreSQL standby<br/>node3")]
        sen{{"3 Sentinels<br/>quorum 2"}} -. "watch,<br/>promote" .-> rm[("Redis master")]
        rm -- "replication" --> rr[("Redis replica")]
        sftp[("SFTP tree<br/>node1")] -- "lsyncd → rrsync<br/>over wg1" --> mirror[("SFTP mirror<br/>node3")]
    end
    subgraph nightly ["Nightly archives: the only real backup"]
        direction LR
        exp["23:40 · node3<br/>SQLite .backup<br/>gitea dump"] -- "pulled via<br/>rrsync" --> job["00:00–00:15 · node1<br/>backup job finds today's<br/>primary and master first<br/>+ RabbitMQ definitions<br/>+ WireGuard config"]
        job --> arc[("AES-256 archive<br/>node1 · 21 kept<br/>verified by decrypt")]
        arc --> copy[("copy on node3<br/>30 days")]
        copy -.-> off[/"off-site copy:<br/>not yet"/]
    end
    live -. "pg_dump + roles from the primary<br/>RDB from the master · SFTP tree" .-> nightly
~~~

A standby and a mirror copy a `DROP TABLE` within milliseconds, so only the encrypted
archives count as backups. The original archive format was restore-drilled on
2026-11-20; the extended format has not been drilled yet, and there is no off-site copy
(roadmap M8).

## When a machine dies

Redis fails over by itself. This is what happened when node2 was lost for 28 hours on
2026-11-18:

~~~mermaid
sequenceDiagram
    autonumber
    participant C as Sentinel-aware client
    participant S as Sentinels on node1, node2, node3
    participant M as Redis on node2
    participant R as Redis on node3
    C->>S: which node is the homelab-redis master?
    S-->>C: node2
    C->>M: reads and writes
    Note over M: node2 is lost
    S->>S: no answer for 20 s, 2 of 3 Sentinels agree
    S->>R: promote (REPLICAOF NO ONE)
    S-->>C: the master is now node3
    C->>R: reads and writes
    Note over M: node2 comes back
    S->>M: demote (REPLICAOF node3)
    M->>R: resynchronise as a replica
~~~

PostgreSQL is promoted by hand, on purpose. Promoting while the old primary is merely
unreachable creates two primaries. In the same outage the standby was left alone and
resumed streaming by itself when node2 returned. Runbooks:
[Redis failover](docs/runbooks/redis-failover-and-reboots.md),
[PostgreSQL promotion](docs/runbooks/postgres-promotion.md).

## Rebuilding a machine from nothing

~~~mermaid
flowchart TB
    bare(["bare machine"]) --> stick["installer stick built for this machine<br/>disk serial · SSH keys · Wi-Fi<br/>encrypted recovery bundle"]
    stick --> check{"right disk serial,<br/>and the owner<br/>confirms the wipe?"}
    check -- no --> safe(["stops, nothing written"])
    check -- yes --> os["unattended Ubuntu Server 26.04<br/>whole-disk LVM · key-only SSH"]
    os --> restore["controller only: sudo homelab-restore<br/>one password brings back SSH keys,<br/>vault password, repository and inventory,<br/>Claude Code"]
    restore --> converge["ansible-playbook site.yml<br/>WireGuard · firewall · Docker · services"]
~~~

The stick for node4 was tested end to end in a UEFI virtual machine on 2026-11-21:
offline install, restore, and Ansible reaching node1–3. The test found three bugs before
any hardware was wiped. On 2026-11-22 it rebuilt the real laptop: from node4, Ansible
reaches all four machines and the vault opens. See [installer/README.md](installer/README.md).

## HomeLab Pulse, my own monitoring

~~~mermaid
flowchart LR
    subgraph each ["every machine"]
        src["/proc · /sys · SMART snapshots<br/>NGINX · SQLite · Redis · PostgreSQL"] --> agent["pulse-agent<br/>every 15 s<br/>unprivileged"]
        agent --> q[("outbox<br/>on disk")]
    end
    q -- "POST over wg1<br/>token per machine" --> api["receiver (.NET)<br/>outside this repository<br/>history · live dashboard"]
~~~

A small Python agent, standard library only, measures each host and its services. It queues
samples on disk and delivers them to a private receiver, which keeps the history and
drives a live dashboard. The agent, its JSON contracts and its Ansible role are here,
with unit tests. **It is not deployed yet**: machines join once the .NET receiver
accepts the contract. How it works, with sequence diagrams and flowcharts:
[apps/homelab-pulse/README.md](apps/homelab-pulse/README.md).

## Status on 2026-11-24

**Running, and checked against the live systems:**

- the WireGuard mesh and its overlay guard;
- PostgreSQL 16 primary plus standby, and Redis 7.4 with three Sentinels;
- the Caddy edge, SFTP with its mirror, RabbitMQ, and the nginx/SQLite app host;
- Vault (initialised, on the overlay only, no secrets in it yet; unsealed by hand after
  every restart);
- Gitea with its Actions runner (registered, no pipelines yet);
- nightly encrypted backups with an off-node copy;
- key-only SSH, Fail2Ban, SMART monitoring and watchdogs on every server.

**In development:** HomeLab Pulse (see above).

**node4** was reinstalled headless on 2026-11-22 and is the controller again. Since
2026-11-24 it has the firewall, the overlay guard and Fail2Ban, and it is on wg1; it has
no Docker yet. Its SSH (2345, keys only) is the one port the router forwards.

**Next:** finish bringing node4 into the platform (Docker, a reboot). After that come
WireGuard remote access, and then MongoDB, Vaultwarden, LocalStack and monitoring on
node4. Open security gaps are listed in [docs/security/posture.md](docs/security/posture.md).

## Rebuild and operate

~~~bash
# A bare machine: build its installer stick (Wi-Fi, SSH keys, optional recovery bundle)
scripts/build-installer-iso.py --help                    # see installer/README.md

# Converge every layer, in order (or run one playbook), from ansible/
cd ansible && ~/homelab/.venv/bin/ansible-playbook playbooks/site.yml
~~~

[ansible/README.md](ansible/README.md) lists the playbooks, roles and run order.
[docs/runbooks/](docs/runbooks/) holds the runbooks: Vault initialisation, Redis
failover and rolling reboots, PostgreSQL promotion, restoring a backup, and recovering
the controller.

## Repository layout

~~~text
ansible/      roles and playbooks: all machine and service configuration
apps/         application code: HomeLab Pulse (monitoring agent and its contracts)
inventory/    example inventory, secret-variable template, image pins
              (the real inventory and the secrets are git-ignored)
installer/    unattended Ubuntu installer stick and the controller restore tool
scripts/      build and write the stick, recovery bundle, homelab password rotation
docs/         handover context, architecture, security, roadmap, runbooks
.claude/      Claude Code agents, skills, rules and safety hooks
~~~

## Documentation map

| Read | For |
|---|---|
| [docs/PROJECT-CONTEXT.md](docs/PROJECT-CONTEXT.md) | **Start here.** Hardware, placement reasoning, traps, incidents, current state |
| [docs/architecture/overview.md](docs/architecture/overview.md) | Topology, data flows, service catalog |
| [docs/roadmap.md](docs/roadmap.md) | Goal, milestones, decisions still open |
| [docs/security/posture.md](docs/security/posture.md) | Verified controls and open gaps |
| [docs/security/secrets-management.md](docs/security/secrets-management.md) | Where every secret lives and how it comes back |
| [ansible/README.md](ansible/README.md) | Roles, playbooks, run order |
| [installer/README.md](installer/README.md) | From bare metal to a reachable host |
| [apps/homelab-pulse/README.md](apps/homelab-pulse/README.md) | HomeLab Pulse: how the monitoring agent works |
| [docs/claude-code.md](docs/claude-code.md) | How Claude Code is set up to work on this repository |

## Not part of this platform

Early revisions of this repository planned Jenkins, Microsoft SQL Server, Terraform and
seven machines. Jenkins and SQL Server were dropped to fit the hardware. Terraform was
dropped because nothing provider-managed exists yet. Only four machines exist. Kubernetes
is not used: four 2-core machines are served well enough by Docker Compose. See
[PROJECT-CONTEXT §6](docs/PROJECT-CONTEXT.md#6-what-was-cut-and-why).

## Security

The repository is public. No secrets, keys, real inventory or state are committed; see
[SECURITY.md](SECURITY.md).

## License and author

MIT, see [LICENSE](LICENSE).

**Vladimir Petukhov**: .NET, backend, architecture. Building a reproducible
self-hosted platform from bare metal upward.
