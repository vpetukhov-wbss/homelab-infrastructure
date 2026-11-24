# Ansible

All machine and service configuration lives here. The roles are idempotent and **check
their own effect**: they assert the live state (`sshd -T`, iptables, replication status,
Sentinel quorum) instead of trusting that a task ran.

## Running

~~~bash
cd ansible                                   # ansible.cfg points at ../inventory/hosts.yml
V=~/homelab/.venv/bin
$V/ansible homelab -m ping                   # host keys are pinned, so there are no prompts
$V/ansible-playbook playbooks/site.yml       # every layer, in order
$V/ansible-playbook playbooks/edge.yml       # or a single layer
~~~

- **Inventory and secrets.** The real inventory (`inventory/hosts.yml`) and the secrets
  (`inventory/group_vars/homelab/*.yml`, ansible-vault) are git-ignored. To start from
  scratch, copy [`inventory/hosts.example.yml`](../inventory/hosts.example.yml) and
  follow [`inventory/secrets.example.yml`](../inventory/secrets.example.yml).
- **Vault password.** Read from `~/.homelab_vault_pass` (see `ansible.cfg`). Change it
  with `scripts/set-homelab-password.sh`.
- **Image versions** are pinned in
  [`inventory/group_vars/all/images.yml`](../inventory/group_vars/all/images.yml).
- **Host keys** are pinned in `~/.ssh/known_hosts` under the inventory name
  (`HostKeyAlias`), and `host_key_checking` is on.

## Inventory groups

| Group | Host | Installed there |
|---|---|---|
| `edge` | node1-gateway | Caddy, Vault, SFTP, RabbitMQ, Sentinel, backup job |
| `data_primary` | node2 | PostgreSQL primary + Redis, Sentinel |
| `data_replica` | node3-data | PostgreSQL standby + Redis, Sentinel, app host, Gitea + runner, sync account |
| `controller` | node4-laptop (local connection) | Firewall, SSH hardening, Fail2Ban and the persistent session, until node4 is onboarded (roadmap M3) |

A group name says which role was **installed** on the host. Failover moves the live
roles; today the Redis master is on node3. That is why the roles discover the topology
instead of trusting names.

## Playbooks, in `site.yml` order

| Playbook | Hosts | Roles | Notes |
|---|---|---|---|
| `base.yml` | node2 and node4; node1–4; node4 | `docker`; `base_packages`, `host_hardening`; `wifi_power_save`, `lxd_disabled` | Docker was preinstalled on node1 and node3. On node4 the account stays out of the `docker` group |
| `overlay.yml` | node1–4 | `wireguard` | One play, so every peer's key is known before templating; `--limit` does not work. Reloads `wg1`, never restarts it |
| `security.yml` | node1–4 | `firewall`, `ssh_hardening` | Asserts the live rules, the overlay guard and `sshd -T`. On node4 follow [the runbook](../docs/runbooks/firewall-with-rollback.md) |
| `fail2ban.yml` | node1–4 | `fail2ban` | Includes the laptop: its SSH is forwarded from the Internet |
| `data-primary.yml` | node2 | `redis_topology`, `postgres_primary` | Finds the live Redis master first |
| `data-replica.yml` | node3 | `redis_topology`, `postgres_replica` | Same; bootstraps the standby with `pg_basebackup` |
| `sentinels.yml` | node1–3 | `redis_topology`, `redis_sentinel` | One Sentinel at a time, then a quorum check |
| `edge.yml` | node1 | `edge` | Restarts Caddy when the Caddyfile changes |
| `app-host.yml` | node3 | `app_host` | |
| `rabbitmq.yml` | node1 | `rabbitmq` | |
| `gitea.yml` | node3 | `gitea` | Registers the runner and waits until it is declared |
| `sftp-mirror.yml` | node3; node1 | `node_exports`; `sftp_mirror` | Sync account and exports first, then the mirror (needs `edge.yml`) |
| `backup.yml` | node1 | `backup` | Runs after every service: it snapshots what they created |
| `mongodb.yml` | node4 | `mongodb` | Written, not deployed and not in `site.yml` yet (roadmap M5) |
| `controller.yml` | node4 | `persistent_session` | User-level, needs no become password |
| `restore-drill.yml` | node4 | `restore_drill` | Not in `site.yml`: deploys nothing. About monthly |

On a machine built from scratch, this order matters. node3's exports snapshot Gitea and
the app host's SQLite, and the backup pulls those exports.

## Roles

| Role | What it does |
|---|---|
| `base_packages` | WireGuard, ufw, rsync, lsyncd, chrony; IPv4 forwarding |
| `host_hardening` | See [`host_hardening`](#host_hardening) below |
| `docker` | Docker Engine + Compose from Docker's apt repository |
| `wireguard` | The `wg1` full mesh. Keys are generated on each node and never leave it |
| `firewall` | See [`firewall`](#firewall) below |
| `ssh_hardening` | Key-only SSH and no forwarding. The drop-in is named `00-` so cloud-init cannot override it. Asserts `sshd -T` |
| `fail2ban` | sshd jail via ufw; the port is read from `sshd -T`; only loopback, the overlay and the controller are exempt |
| `redis_topology` | Asks every Sentinel who the master is, and fails unless they agree |
| `postgres_primary` | PostgreSQL 16 primary (tuned for HDD, slot capped) plus this node's Redis. Its `tasks/redis.yml` is shared with the replica |
| `postgres_replica` | PostgreSQL standby (`pg_basebackup`, slot `node3_slot`, its own `pg_hba`) plus Redis |
| `redis_sentinel` | Sentinel at quorum 2. Config seeded once; tunables set through `SENTINEL SET` |
| `edge` | Caddy (the only public listener), Vault (loopback until initialised), SFTP |
| `sftp_mirror` | lsyncd to the rrsync-confined `homelab-sync` account on node3, verified with a canary file |
| `node_exports` | On node3: the sync account, plus SQLite and Gitea snapshots published for the backup |
| `app_host` | nginx + SQLite for applications, on the overlay only |
| `rabbitmq` | RabbitMQ 3.13 + management UI, on the overlay only, with an absolute memory cap |
| `gitea` | Gitea + act_runner. Jobs run without the Docker socket and with CPU/RAM/PID caps |
| `backup` | See [`backup`](#backup) below |
| `wifi_power_save` | On node4: Wi-Fi power saving off, bound to the interface. Asserts the live setting |
| `lxd_disabled` | On node4: empties the `lxd` group and masks the LXD installer's socket. Asserts both |
| `restore_drill` | Restores the newest archive into throwaway containers without a network, in a RAM mount, and checks every part ([runbook](../docs/runbooks/backup-restore.md)) |
| `persistent_session` | On node4: every interactive SSH login attaches to the tmux session `main`, which outlives the connection. Asserts that logind does not kill it |

### `host_hardening`

- smartd;
- a hardware watchdog (iTCO, falling back to softdog), fed by systemd;
- sysctls that panic on a lockup;
- Docker daemon settings: log rotation and `live-restore`;
- Docker starts after `wg1`.

### `firewall`

- ufw default deny;
- SSH allowed only from the LAN and `wg1`, on the port `sshd -T` reports. The one exception is
  node4, open to any source until roadmap M4 (`inventory/group_vars/controller/firewall.yml`);
- the **overlay guard**: a `raw`-table drop of overlay traffic that did not arrive on
  `wg1`, plus an `unreachable` route for the address range that collides with the ISP.

### `backup`

- runs nightly and writes an encrypted archive that it verifies itself;
- discovers the PostgreSQL primary and Redis master at run time;
- copies the archive to node3.

## Conventions

- **Never `0.0.0.0`.** Services bind the overlay address, and roles that publish ports
  assert it.
- **Mount directories, not single files,** for anything that a service or `template`
  rewrites. A rename onto a single-file mount fails, or lands on an inode the container
  never sees.
- **Seed once, then let the service own the config** (Redis, Sentinel). Change tunables
  through the service's API.
- **Restart from a handler when a bind-mounted config changes.** `docker compose up -d`
  does not recreate the container for that.
- **Discover, don't assume.** The PostgreSQL primary and the Redis master are queried at
  run time.
- **Debugging traps:** [`docs/PROJECT-CONTEXT.md` §7](../docs/PROJECT-CONTEXT.md#7-traps-discovered-the-hard-way)
  lists the ones that cost real time.
