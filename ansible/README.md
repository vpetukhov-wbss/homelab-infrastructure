# Ansible

Ansible owns machine and service configuration.

Planned reusable roles:

- base
- users
- security
- firewall
- docker
- wireguard
- node_exporter
- caddy
- nginx
- postgres
- mongodb
- redis
- sqlserver
- vault
- jenkins
- gitea
- sftp
- restic
- prometheus
- grafana
- localstack

Roles should be idempotent, composable and driven by inventory/group variables rather than copied per host.

A full site playbook will eventually allow a new node to progress from bootstrap Ubuntu to its assigned role with one controlled workflow.

---

## Implemented roles (status: live)

| Role | Purpose | Applied to |
|------|---------|-----------|
| `docker` | Docker Engine + Compose plugin (official repo, works on 22.04 & 24.04) | node2 (others pre-had it) |
| `base_packages` | wireguard, ufw, rsync, lsyncd, chrony; enables IPv4 forwarding | all 3 ThinkCentres |
| `wireguard` | wg1 overlay mesh (10.44.0.0/24), full mesh, keys generated per node | node1/node2/node3 |
| `firewall` | ufw default-deny; allows only SSH + wg port + wg1 interface; edge also 80/443 | all 3 ThinkCentres |
| `ssh_hardening` | key-only SSH (PasswordAuthentication no), no root login | all 3 ThinkCentres |
| `postgres_primary` | Postgres 16 primary (HDD-tuned) + Redis 7 primary; overlay-only bind | node2 |

Playbooks: `base.yml`, `overlay.yml`, `security.yml`, `data-primary.yml`.
Run from `ansible/` with the venv's `ansible-playbook`; real inventory lives in the
git-ignored `inventory/hosts.yml`, secrets in the git-ignored ansible-vault files
under `inventory/group_vars/`.

## Realistic-architecture decisions (hardware-constrained)

The original full-stack plan was trimmed to fit the **actual** hardware (3 nodes with
2-core CPUs / 3.7 GB RAM / spinning HDDs, plus the controller laptop with an SSD):

- **Databases = PostgreSQL + Redis only.** MongoDB cut (RAM-hungry WiredTiger + v4.4 EOL);
  SQL Server cut. Each keeps a primary + async streaming replica on separate physical nodes.
- **CI = Gitea Actions, not Jenkins** (Jenkins JVM ~1 GB+ did not fit alongside the DBs).
- **Mixed OS kept** (node1/node3 on 22.04, node2 on 24.04) — no reinstalls; roles are
  written to work on both.
- **All DB ports bind only to the WireGuard overlay IP** (10.44.0.x), never 0.0.0.0 —
  nothing is reachable from the LAN or the internet. Only the edge node exposes 80/443.
- **Honest performance ceiling:** spinning HDDs limit DB random I/O; the single
  highest-ROI upgrade would be one SATA SSD in the Postgres-primary node.

Node→role map: node1 = edge (Caddy + WireGuard hub + Vault + SFTP-primary + Redis Sentinel),
node2 = Postgres/Redis primary, node3 = all replicas + SFTP mirror, node4/laptop =
Gitea + Gitea Actions (on its SSD) + Ansible controller.
