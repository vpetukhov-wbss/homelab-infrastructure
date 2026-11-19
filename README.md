# HomeLab Infrastructure

![Ubuntu](https://img.shields.io/badge/Ubuntu%20Server-22.04%20LTS-E95420?logo=ubuntu&logoColor=white)
![Terraform](https://img.shields.io/badge/IaC-Terraform-844FBA?logo=terraform&logoColor=white)
![Ansible](https://img.shields.io/badge/Configuration-Ansible-EE0000?logo=ansible&logoColor=white)
![Docker](https://img.shields.io/badge/Runtime-Docker-2496ED?logo=docker&logoColor=white)
![WireGuard](https://img.shields.io/badge/Overlay-WireGuard-88171A?logo=wireguard&logoColor=white)
![Gitea](https://img.shields.io/badge/SCM-Gitea-609926?logo=gitea&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-blue)

> A production-inspired, self-hosted bare-metal platform built from repurposed hardware and automated from operating-system installation to application deployment.

This repository documents and automates a multi-node HomeLab designed for real workloads, infrastructure learning, DevOps experimentation, CI/CD, high-availability data services, secure remote access, observability, backups, and disaster recovery.

The long-term objective is simple:

**From bare metal to a configured node with one repeatable workflow.**

The repository is intentionally public. Architecture, automation, examples, policies, runbooks, and configuration templates belong here. Real credentials, private keys, tokens, Terraform state, recovery keys, and environment-specific secrets do not.

---

## Why this project exists

The lab is intended to behave like a small production platform rather than a collection of unrelated Docker containers.

Core design goals:

- Rebuildable bare-metal nodes.
- A single Ubuntu Server baseline (currently 22.04 LTS on all three nodes).
- Infrastructure as Code.
- Role-based configuration rather than machine-specific snowflakes.
- Encrypted node-to-node communication through WireGuard.
- A dedicated edge/gateway security boundary.
- Database replication and explicit failover procedures.
- Centralized secrets management with HashiCorp Vault.
- Self-hosted CI/CD with Gitea Actions (Jenkins was cut — it did not fit in 3.7 GB per node).
- Self-hosted source control with Gitea.
- Public GitHub repository as upstream documentation and portfolio.
- Automated backups with tested recovery procedures.
- Monitoring, metrics, health checks, and operational runbooks.
- Hardware-independent role definitions so nodes can be replaced or reassigned.

---

## Platform architecture

> **This section describes the original target architecture, not what is deployed.**
> The lab was scoped down to the four machines that actually exist; MongoDB, SQL
> Server, RabbitMQ, Jenkins and the dedicated DevOps node were cut to fit 2-core /
> 3.7 GB nodes. For the deployed topology, role-to-host mapping and verified state,
> see **[docs/architecture/overview.md](docs/architecture/overview.md)**; for the
> verified security controls and open gaps, see
> **[docs/security/posture.md](docs/security/posture.md)**.

~~~mermaid
flowchart TB
    Internet((Internet))
    Router[ISP Router]
    Edge[Edge / Gateway Node<br/>WireGuard · Caddy · Load Balancer]
    WG{{WireGuard Overlay Network}}

    DataP[Data Primary<br/>PostgreSQL · MongoDB · Redis]
    DataR[Data Replica / Backup<br/>PostgreSQL Replica · Mongo Secondary · Redis Replica · SFTP · Restic]
    Mgmt[Management / App Node<br/>Docker · NGINX · Vault · Monitoring · Portainer]
    SqlP[SQL Primary<br/>Microsoft SQL Server]
    SqlR[SQL Secondary<br/>Microsoft SQL Server Replica]
    DevOps[Future DevOps Control Plane<br/>Gitea · Jenkins · Runners]

    Internet --> Router --> Edge
    Edge --> WG
    WG --> DataP
    WG --> DataR
    WG --> Mgmt
    WG --> SqlP
    WG --> SqlR
    WG -. future .-> DevOps

    DataP <-->|replication| DataR
    SqlP <-->|replication / AG| SqlR
    Edge -->|reverse proxy / load balancing| Mgmt
~~~

### Logical roles

| Role | Primary responsibility | Planned services |
|---|---|---|
| Edge / Gateway | Only controlled ingress point | WireGuard, Caddy, TLS, reverse proxy, load balancing, optional Cloudflare Tunnel |
| Data Primary | Primary open-source data services | PostgreSQL primary, MongoDB primary, Redis primary |
| Data Replica / Backup | Replication and recovery | PostgreSQL standby, MongoDB secondary, Redis replica, SFTP, Restic |
| Management / App | Runtime and platform management | Docker workloads, NGINX, Vault member, Portainer, Prometheus, Grafana |
| SQL Primary | Dedicated Microsoft data workload | SQL Server primary |
| SQL Secondary | SQL resilience | SQL Server secondary / availability replica |
| DevOps Control Plane | Future dedicated automation node | Gitea, Jenkins controller, build runners/agents |

Initially Jenkins and Gitea may share the Management node. When another machine is available they can move to the dedicated DevOps Control Plane without changing the repository model.

---

## Network model

The physical LAN is the underlay. Service-to-service traffic is designed to use an encrypted WireGuard overlay.

Example public documentation addresses:

| Role | WireGuard address |
|---|---:|
| Edge | 10.44.0.1 |
| Data Primary | 10.44.0.2 |
| Data Replica | 10.44.0.3 |
| Management / App | 10.44.0.4 |
| SQL Primary | 10.44.0.5 |
| SQL Secondary | 10.44.0.6 |
| Future DevOps | 10.44.0.7 |

These are design defaults, not a disclosure of the real LAN.

Security principle:

~~~text
Internet
   |
   v
Edge / Gateway
   |
   +---- public application routes only
   |
   +---- WireGuard administrative access
             |
             v
       Private platform
       databases / Vault /
       Jenkins / Gitea /
       Grafana / Portainer
~~~

Database and management ports must never be intentionally exposed directly to the public Internet.

---

## Technology stack

> **Target stack.** Several entries below are not deployed — notably Jenkins,
> MongoDB and SQL Server, which were cut. See
> [docs/architecture/overview.md](docs/architecture/overview.md) for what actually runs.

### Bare metal and operating system

- Ubuntu Server 22.04 LTS (deployed on all three nodes; 24.04 is the eventual target)
- Ubuntu Autoinstall / cloud-init
- SSH keys only
- unattended security updates
- UFW/nftables host firewall
- Fail2Ban where appropriate

### Infrastructure automation

- **Terraform** — desired infrastructure state, provider-managed resources, Vault configuration, DNS/Cloudflare integrations, identities and infrastructure relationships.
- **Ansible** — operating-system configuration, packages, users, Docker, WireGuard, firewalls, files, systemd units, database/service configuration and repeatable node roles.
- **Jenkins** — CI/CD and operational orchestration.
- **GitHub** — public upstream repository and portfolio.
- **Gitea** — self-hosted source control, internal repositories and optional GitHub mirrors.

### Runtime and edge

- Docker Engine
- Docker Compose
- Caddy
- NGINX
- WireGuard
- optional Cloudflare Tunnel
- Portainer

### Data services

- PostgreSQL primary + streaming replica
- Microsoft SQL Server primary + secondary
- MongoDB replica set
- Redis primary + replica + Sentinel design
- LocalStack for S3-compatible development/testing scenarios
- SFTP for controlled transfers

### Platform services

- HashiCorp Vault
- Jenkins
- Gitea
- Prometheus
- Grafana
- Restic
- node/container metrics exporters

---

## Provisioning model

A node is created in layers.

~~~mermaid
flowchart LR
    A[Bare Metal] --> B[Ubuntu Autoinstall USB]
    B --> C[Bootstrap Ubuntu]
    C --> D[Terraform]
    D --> E[Ansible]
    E --> F[WireGuard + Firewall]
    F --> G[Docker + Node Role]
    G --> H[Validation]
    H --> I[Ready]
~~~

The installation media stays generic. It does not permanently decide whether a machine is a database server, gateway, or CI node.

The desired workflow is:

~~~bash
./homelab plan data-primary
./homelab apply data-primary
./homelab validate data-primary
~~~

The wrapper will eventually coordinate Terraform and Ansible while keeping each tool responsible for the work it is best suited for.

---

## CI/CD model

> **Not deployed.** This describes the intended pipeline. Jenkins was cut; CI/CD is
> planned on Gitea Actions, and neither Gitea nor its runner is running yet.

~~~mermaid
flowchart LR
    Dev[Developer] --> Git[GitHub / Gitea]
    Git --> Jenkins[Jenkins]
    Jenkins --> Checks[Validate · Lint · Test · Scan]
    Checks --> Plan[Terraform Plan]
    Plan --> Approval{Manual approval}
    Approval --> Apply[Terraform Apply]
    Apply --> Ansible[Ansible]
    Ansible --> Deploy[Deploy / Configure]
    Deploy --> Health[Health checks]
~~~

Infrastructure changes should be validated before deployment. Destructive or production-like operations require explicit approval.

Planned checks include:

- terraform fmt
- terraform validate
- tflint
- ansible-lint
- yamllint
- shellcheck
- gitleaks
- container/image security scanning
- configuration validation
- smoke and health tests

---

## Source control strategy

GitHub and Gitea serve different purposes.

### GitHub

- Public canonical project documentation.
- Public Infrastructure-as-Code examples.
- Portfolio and collaboration.
- Pull requests and issue tracking.
- No real infrastructure secrets.

### Gitea

- Self-hosted internal Git service.
- Private application repositories where required.
- Internal mirrors.
- Jenkins integration.
- Continued operation inside the lab when external services are unavailable.

A future dedicated DevOps node can host Gitea, Jenkins and build agents. Until that node exists, these services may be colocated on the management node with strict resource limits.

---

## Secrets strategy

**Vault is the runtime source of truth.**

Examples of secrets that belong in Vault:

- database credentials
- application secrets
- API keys
- deployment credentials
- service tokens
- S3 credentials
- generated dynamic credentials where supported

GitHub Secrets are reserved for workflow-specific bootstrap credentials that cannot be replaced by OIDC or short-lived identities.

SOPS + age is planned only for carefully controlled bootstrap/disaster-recovery material when encrypted configuration must live in Git.

Never commit:

- .env files with real values
- Terraform state
- database passwords
- Vault root/recovery/unseal material
- WireGuard private keys
- SSH private keys
- TLS private keys
- access tokens
- backup encryption keys
- age private identities

See docs/security/secrets-management.md and SECURITY.md.

---

## Repository layout

~~~text
homelab-infrastructure/
├── README.md
├── CLAUDE.md
├── SECURITY.md
├── CONTRIBUTING.md
├── LICENSE
│
├── installer/
│   └── README.md
│
├── inventory/
│   └── nodes.example.yml
│
├── terraform/
│   └── README.md
│
├── ansible/
│   └── README.md
│
├── docs/
│   ├── architecture/
│   │   └── overview.md
│   ├── platform/
│   │   ├── gitea.md
│   │   └── jenkins.md
│   ├── security/
│   │   └── secrets-management.md
│   └── roadmap.md
│
├── docker/                 # service stacks and common Compose fragments
├── vault/                  # policies, auth configuration and templates
├── monitoring/             # Prometheus/Grafana configuration
├── scripts/                # bootstrap, validation, backup and restore helpers
└── .github/                # repository automation
~~~

Directories are introduced gradually as implementation starts. The repository should remain understandable before it becomes large.

---

## High availability versus backup

Replication is not a backup.

This project intentionally treats these as separate concerns:

- PostgreSQL streaming replication protects service continuity.
- SQL Server replication/Availability Groups protect service continuity.
- MongoDB replica set protects availability.
- Redis replication/Sentinel protects availability.
- Restic/database-native backups protect against corruption, accidental deletion and operator error.
- Offline/external backup copies protect against total-lab loss.

Every stateful service will have both a replication design and a documented restore procedure.

---

## Security principles

1. Deny inbound traffic by default.
2. Prefer WireGuard addresses for administrative and backend traffic.
3. Expose only the gateway where possible.
4. Use SSH keys; disable password SSH authentication.
5. Keep database interfaces private.
6. Run containers with least privilege.
7. Avoid privileged containers unless technically unavoidable and documented.
8. Keep secrets out of Git.
9. Encrypt backups.
10. Patch hosts and images regularly.
11. Scan commits for accidental secrets.
12. Document every intentional public exposure.
13. Make recovery reproducible, not tribal knowledge.

---

## Current project status

**Phase: data platform and edge deployed; secrets and CI/CD outstanding.**
Verified against the running systems on 2026-11-19 — see
[docs/architecture/overview.md](docs/architecture/overview.md) for detail.

Deployed and verified:

- Four machines inventoried: three ThinkCentres (2-core, ~3.7 GB RAM, HDD) and a laptop.
- WireGuard `wg1` overlay, `10.44.0.0/24`, full mesh across the three wired nodes.
- PostgreSQL 16 primary with an asynchronous streaming standby, held by a replication
  slot. Verified end to end: writes replicate, the standby refuses writes.
- Redis 7 with a replica and three Sentinels at quorum 2. Automatic failover and
  automatic demotion of a returning master were both exercised under a real outage.
- Edge on node1: Caddy serving `:80`, SFTP, and Vault 1.18 (running but uninitialised).
- SFTP mirror node1 → node3 over the overlay via lsyncd, verified including deletions.
- `ufw` default-deny and key-only SSH on all four machines, verified against the
  effective `sshd -T` configuration rather than the intended one.
- No database port bound to `0.0.0.0` anywhere; all bind the overlay address.

Outstanding:

- Vault `operator init` — Vault holds no secrets and is not yet an operational dependency.
- Gitea and Gitea Actions on the laptop (role written, not deployed).
- Laptop not yet on the WireGuard overlay.
- No public ingress: no router forward to the edge, and no TLS site on `:443`.
- Monitoring, metrics and backups beyond the SFTP mirror.
- Open security gaps are tracked in [docs/security/posture.md](docs/security/posture.md).

Explicitly cut from the original plan: MongoDB, Microsoft SQL Server, RabbitMQ,
Jenkins, and the dedicated DevOps node.

---

## Roadmap

### Phase 1 — Foundation
Repository structure, documentation, inventory schema, security model and tool boundaries.

### Phase 2 — Bare-metal bootstrap
Reproducible Ubuntu Server Autoinstall media and first-boot bootstrap.

### Phase 3 — Base platform
Terraform foundation, Ansible roles, Docker, WireGuard, firewalling, users and host hardening.

### Phase 4 — Data platform
PostgreSQL, MongoDB, Redis, SQL Server and replication.

### Phase 5 — Platform services
Vault, Jenkins, Gitea, Portainer, LocalStack, SFTP, monitoring.

### Phase 6 — Application hosting
Caddy/NGINX routing, load balancing, application deployment workflows and health checks.

### Phase 7 — Recovery engineering
Restic, database-native backups, retention, external copies, restore automation and failure drills.

### Phase 8 — DevOps control plane
Move Jenkins/Gitea/runners to a dedicated node when additional hardware is available.

See docs/roadmap.md for the working implementation plan.

---

## Important note

This project intentionally distinguishes **production-inspired** from **production-certified**.

A HomeLab can model strong production practices, but high availability still depends on physical redundancy, quorum, storage durability, power, networking, licensing and tested operational procedures.

The goal is not to hide those limitations. The goal is to understand and engineer around them.

---

## Claude Code engineering team

The repository includes a shared Claude Code setup under `.claude/` so AI-assisted work follows the same architecture, security, and operational standards as human changes.

Specialists cover platform architecture, Linux administration, networking/security, data reliability, Terraform/Ansible, Jenkins/Gitea, SRE/DR, security review, and technical project management. Reusable workflows include `/plan-change`, `/bootstrap-node`, `/security-review`, `/incident-response`, and `/dr-drill`.

See [docs/claude-code.md](docs/claude-code.md).

---

## License

MIT — see LICENSE.

---

## Author

**Vladimir Petukhov**

.NET / Backend / Architecture / DevOps enthusiast building a reproducible self-hosted platform from bare metal upward.
