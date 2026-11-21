# HomeLab Infrastructure

![Ubuntu Server](https://img.shields.io/badge/Ubuntu%20Server-22.04%20%7C%2024.04%20%7C%2026.04-E95420?logo=ubuntu&logoColor=white)
![Ansible](https://img.shields.io/badge/Configuration-Ansible-EE0000?logo=ansible&logoColor=white)
![Docker](https://img.shields.io/badge/Runtime-Docker%20Compose-2496ED?logo=docker&logoColor=white)
![WireGuard](https://img.shields.io/badge/Overlay-WireGuard-88171A?logo=wireguard&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16%20primary%20%2B%20standby-4169E1?logo=postgresql&logoColor=white)
![Redis](https://img.shields.io/badge/Redis-7.4%20%2B%20Sentinel-DC382D?logo=redis&logoColor=white)
![Gitea](https://img.shields.io/badge/Git%20%2B%20CI-Gitea%20Actions-609926?logo=gitea&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-blue)

A self-hosted platform on four second-hand machines, configured entirely with Ansible
and run by one person. The repository is also an honest record of how it was built,
including what went wrong.

## The goal

One owner, four machines, and:

- **the services my applications need**: replicated PostgreSQL and Redis, RabbitMQ,
  SQLite and SFTP today; **MongoDB** and an AWS emulator (**LocalStack**) next;
- **self-hosted Git with CI**, application hosting behind a single edge, and a password
  manager of my own (**Vaultwarden**);
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
| Runs | **Edge**: Caddy, Vault, SFTP, RabbitMQ, Sentinel, backups | PostgreSQL **primary**, Redis, Sentinel | PostgreSQL standby, Redis, Sentinel, app host, Gitea + CI runner, SFTP mirror, backup copy | **Controller** (Ansible, Claude Code). Being reinstalled as a server for MongoDB, Vaultwarden, LocalStack and monitoring |

The CPUs have two cores, each server has about 3 GB of usable RAM, and the disks spin.
**Disk I/O is the ceiling**, and containers do not change that. Why each service sits
where it does:
[docs/PROJECT-CONTEXT.md §3](docs/PROJECT-CONTEXT.md#3-role-assignment-and-why-each-one-landed-where-it-did).

## How it fits together

~~~mermaid
flowchart LR
    owner([Owner, anywhere]) -. "WireGuard (planned)" .-> n1
    lan[(LAN, treated as hostile)] -->|80 / 443| n1
    subgraph wg1 ["WireGuard overlay wg1 · 10.44.0.0/24 · full mesh"]
      n1["node1 · edge<br/>Caddy · Vault · SFTP · RabbitMQ · Sentinel · backups"]
      n2["node2<br/>PostgreSQL primary · Redis · Sentinel"]
      n3["node3<br/>PostgreSQL standby · Redis · Sentinel<br/>apps · Gitea + runner · SFTP mirror"]
    end
    n4["node4 · controller<br/>Ansible · Claude Code"] -->|SSH| n1 & n2 & n3
    n2 -->|streaming replication| n3
    n1 -->|reverse proxy| n3
    n1 -->|mirror · nightly backup copy| n3
~~~

- **Only node1 listens publicly.** Caddy serves :80 and :443; there is no router
  forward to it yet. Every database, broker, Vault and application port binds to the
  overlay. On top of that, a `raw`-table guard drops overlay traffic that did not arrive
  on `wg1`, because Docker's port publishing bypasses ufw.
- **PostgreSQL failover is manual and Redis failover is automatic** (three Sentinels,
  quorum 2). A real 28-hour outage exercised both.
- **Backups** run nightly, are AES-256 encrypted and verify themselves. They are stored
  on node1 and copied to node3.

Full topology and service catalog:
[docs/architecture/overview.md](docs/architecture/overview.md).

## Status on 2026-11-21

**Running, and checked against the live systems:**

- the WireGuard mesh and its overlay guard;
- PostgreSQL 16 primary plus standby, and Redis 7.4 with three Sentinels;
- the Caddy edge, SFTP with its mirror, RabbitMQ, and the nginx/SQLite app host;
- Vault (initialised, on the overlay only, no secrets in it yet; unsealed by hand after
  every restart);
- Gitea with its Actions runner (registered, no pipelines yet);
- nightly encrypted backups with an off-node copy;
- key-only SSH, Fail2Ban, SMART monitoring and watchdogs on every server.

**Next:** reinstall the laptop as node4 (the installer stick is ready). After that come
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
