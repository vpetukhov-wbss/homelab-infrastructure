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
| `ssh_hardening` | key-only SSH, no root login; drop-in named `00-` so it beats `50-cloud-init.conf`, and asserts the effective `sshd -T` output | all 3 ThinkCentres |
| `postgres_primary` | Postgres 16 primary (HDD-tuned) + Redis 7 primary; overlay-only bind | node2 |
| `postgres_replica` | Postgres 16 streaming standby (pg_basebackup bootstrap, async) + Redis replica | node3 |
| `redis_sentinel` | Redis Sentinel, quorum 2 of 3, monitoring `homelab-redis` | node1/node2/node3 |
| `edge` | Caddy (the only public service) + Vault 1.18 (overlay-only, TLS) + SFTP-primary | node1 |
| `sftp_mirror` | lsyncd one-way mirror of the SFTP tree to the standby, over the overlay; seeds root's `known_hosts` first | node1 → node3 |
| `gitea` | Gitea + act_runner on the laptop's SSD, RAM-capped | node4/laptop |
| `rabbitmq` | RabbitMQ 3.13 + management UI, overlay-only, absolute memory cap | node1 |
| `app_host` | nginx + SQLite for applications, overlay-only, behind the edge | node3 |
| `backup` | nightly encrypted logical backups on a systemd timer, restore-verified | node1 |
| `fail2ban` | sshd jail, bans via ufw, LAN and overlays exempt | all 3 ThinkCentres |

Playbooks, in dependency order: `base.yml`, `overlay.yml`, `security.yml`,
`fail2ban.yml`, `data-primary.yml`, `data-replica.yml`, `sentinels.yml`, `edge.yml`,
`sftp-mirror.yml`, `app-host.yml`, `rabbitmq.yml`, `backup.yml`, `gitea.yml`.

`backup.yml` must run after `app-host.yml` and `rabbitmq.yml`, since it snapshots
what they create.
Run from `ansible/` with the venv's `ansible-playbook`; real inventory lives in the
git-ignored `inventory/hosts.yml`, secrets in the git-ignored ansible-vault files
under `inventory/group_vars/`.

### Operational notes

- **`sftp-mirror.yml` must run after `edge.yml`.** The mirror watches the host-side bind
  mount that the edge role gives the sftp container; it generates a dedicated ed25519
  key on node1 and authorizes it on node3 restricted to node1's overlay IP.
- **The mirror does not preserve ownership** (rsync runs unprivileged on the target).
  Promoting the mirror to primary needs `chown -R 1001:1001` on the target directory.
- **`lsyncd` deletes with `delete = "running"`** — deletions are propagated while it
  watches, but it never bulk-deletes at startup, so an empty source cannot wipe the mirror.
- **Back up `/etc/wireguard/` off-box.** Private keys live only on the nodes otherwise;
  they must never be committed to this public repo.
- **`sshd -t` is not a verification.** It checks syntax and cannot detect a directive
  overridden by an earlier drop-in — which is exactly how password authentication
  stayed enabled on two nodes. `ssh_hardening` now asserts against `sshd -T`.
- **lsyncd runs as root** and the ssh options it passes to rsync do not reliably carry
  `StrictHostKeyChecking`. Without a pre-seeded `/root/.ssh/known_hosts` every sync
  fails with `Host key verification failed` while the playbook still reports success —
  check `/var/log/lsyncd/lsyncd.log`, not just the play recap.
- **Run ansible from this `ansible/` directory.** `ansible.cfg` points at
  `../inventory/hosts.yml`; from anywhere else no inventory is found.
- **A Caddyfile edit alone does nothing.** `admin off` disables the admin API so
  `caddy reload` cannot work, and `docker compose up -d` will not recreate a
  container when only a bind-mounted file changed. The `edge` role restarts Caddy
  from a handler; anything else editing that file must do the same.
- **Never let an application publish on `0.0.0.0`.** Docker's port rules are
  evaluated before ufw's INPUT chain, so ufw will not save you — the bind address in
  the compose file is the control. The `app_host` and `rabbitmq` roles assert this.
- **Fail2Ban's `ignoreip` is load-bearing.** It must always include the LAN and both
  overlays, or a ban can cut off the Ansible controller or a peer node.

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
