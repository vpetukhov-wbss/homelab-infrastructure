# Roadmap

> Last updated **2026-11-21**. A box is ticked only when the result was verified on the
> running systems, not when the code exists. Evidence for the ticked items is in
> [`PROJECT-CONTEXT.md` §9](PROJECT-CONTEXT.md#9-current-state).

## The goal

One owner, four machines:

- **the services my applications need**: replicated PostgreSQL and Redis, RabbitMQ,
  SQLite and SFTP, plus MongoDB and an AWS emulator (LocalStack) for development;
- **self-hosted Git with CI**, application hosting behind one edge, and a password
  manager of my own (Vaultwarden);
- **reachable from anywhere by me, over WireGuard**, and by nobody else;
- **encrypted backups with a tested restore**;
- **every machine rebuildable** from a USB stick and one password, including the
  controller (Ansible and Claude Code), which lives on node4.

## Target placement

| Node | Today | Target |
|---|---|---|
| node1 | Caddy, Vault, SFTP, RabbitMQ, Sentinel, backup job | + WireGuard endpoint for remote access, TLS at the edge |
| node2 | PostgreSQL primary, Redis, Sentinel | unchanged |
| node3 | PostgreSQL standby, Redis, Sentinel, app host, Gitea + runner, SFTP mirror, backup copy | unchanged |
| node4 | the laptop: controller on Wi-Fi, not on the overlay | headless server on `wg1` (10.44.0.4): controller + MongoDB 8, Vaultwarden, LocalStack, monitoring |

**Why node4 gets the new services.** It has the only CPU with AVX, which MongoDB 5.0+
requires. It also has the only SSD and the most RAM (7.3 GB). CI stays on node3 so that
the password manager never shares a host with CI jobs.

**Planning estimate for node4's RAM, to be measured:**

| Service | RAM |
|---|---|
| MongoDB (WiredTiger cache capped at 1 GB) | ~1.5 GB |
| LocalStack | ~1 GB |
| Prometheus + Grafana | ~0.7 GB |
| Vaultwarden | ~0.1 GB |
| OS, Docker, Claude Code, Ansible | ~1.5 GB |
| **Headroom** | **~2.5 GB** |

## Done

- [x] Hardware recorded; roles placed to fit it.
- [x] WireGuard full mesh and the overlay guard. The LAN-route attack is blocked on 14 of
  14 overlay ports.
- [x] Firewall default deny. Key-only SSH, verified with `sshd -T`. Fail2Ban. Ansible
  pins host keys.
- [x] PostgreSQL 16 primary + streaming standby, ready for promotion (`pg_hba`,
  `wal_log_hints`, capped slot).
- [x] Redis 7.4 + 3 authenticated Sentinels with failover state persisted. A real
  failover was survived on 2026-11-18.
- [x] Edge (Caddy), Vault (initialised, on the overlay), SFTP with an rrsync-confined mirror,
  RabbitMQ, app host.
- [x] Gitea + Actions runner on node3. The runner is registered, and jobs run without
  the Docker socket.
- [x] Nightly backups: encrypted, self-verifying, including Gitea, SFTP and WireGuard,
  with a copy on node3.
- [x] SMART monitoring, watchdogs, Docker log rotation, pinned images.
- [x] Unattended installer stick for node4: Wi-Fi, key-only SSH, recovery bundle and
  `homelab-restore`.
- [x] The repository describes the deployed platform and this goal.

## M1 — Publish the current state

- [x] Get a clean `gitleaks` run (8.30.1) over the committable tree and all 19 commits,
  on 2026-11-21. The one finding was prose and is recorded in `.gitleaksignore`.
- [x] Commit the work in three reviewable commits (2026-11-21): the audit fixes, the
  installer and recovery tooling, and the documentation.
- [x] Push to GitHub (2026-11-21). `main` was fast-forwarded to the current state with
  a deploy key that has write access to this repository only:
  `~/.ssh/github-homelab-infrastructure`, SSH alias `github-homelab`.

**Done when** GitHub shows the current state and no deployed claim in it is false.

## M2 — node4 reinstalled as a server

- [x] The owner set the homelab password (`scripts/set-homelab-password.sh`, 2026-11-21).
  All 5 ansible-vault files were re-keyed.
- [x] The recovery bundle was rebuilt with that password
  (`homelab-recovery-20261121-1613.tar.gpg`). Verified: it opens with the new password
  (119 entries) and includes the GitHub deploy key and the laptop's `/etc/wireguard`.
  The old bundle was deleted.
- [ ] The owner copies the bundle off the laptop, together with `RESTORE.txt`.
- [x] The installer stick was rebuilt and verified on 2026-11-21. It carries Wi-Fi, SSH
  on 2345 so the router's forward keeps working, and the bundle. It was written and read
  back; image sha256 `21d64798…`.
- [ ] The owner saves personal data that the bundle does not carry.
- [ ] Install, then run `sudo homelab-restore`.

**Done when:**

- from node4, `ansible homelab -m ping` passes with the pinned host keys and the vault
  opens;
- the owner can still SSH in from outside.

## M3 — node4 joins the platform

- [ ] Add node4 to the mesh as `wg_overlay_ip: 10.44.0.4`, so `wg1` grows from 3 to 4
  peers. The `wireguard` and `redis_topology` roles list the three servers explicitly
  today.
- [ ] Apply `base`, `host_hardening`, `security` and `fail2ban` to node4. First, make the
  `firewall` role read the SSH port from `sshd -T` as `fail2ban` already does. node4
  connects locally and has no `ansible_port`, so the role would allow 22 while sshd
  listens on 2345.
- [ ] Keep node4's SSH reachable from outside until M4 is done.
- [ ] Docker on node4; Wi-Fi power saving off.

**Done when:**

- node4 reaches every node over `wg1`;
- the overlay guard is live on node4, and the LAN-route test is blocked there too.

## M4 — Remote access over WireGuard

- [ ] Decide where the endpoint lives, and what happens to the laptop's legacy `wg0`
  (10.10.10.0/24).
- [ ] Add road-warrior peers for the owner's devices. The router forwards one UDP port.
- [ ] Remove the router's SSH forward to the laptop.

**Done when:**

- from outside, the owner reaches every node's SSH and every overlay service over
  WireGuard;
- the router forwards no TCP port other than 80/443.

## M5 — Services on node4

Each service goes through `/service-onboard`, which produces:

- a role and a placement;
- a bind on the overlay;
- a backup and a restore runbook;
- a row in the service catalog.

- [ ] **MongoDB 8**: a single-node replica set, so transactions and change streams work.
  No HA is claimed. WiredTiger cache capped; `mongodump` in the nightly backup.
- [ ] **LocalStack**: licensing decision first (see below).
- [ ] **Monitoring**: Prometheus and Grafana on node4, `node_exporter` on all four.
  Alerts for at least:
  - disk space;
  - backup age;
  - replication lag;
  - Sentinel quorum;
  - SMART.
- [ ] **Vaultwarden**: behind Caddy with HTTPS (M6, or Caddy's internal CA while it is
  WireGuard-only). Signups off, admin token in ansible-vault. Its SQLite database and
  attachments go into the backup.

**Done when** each service has passed its restore drill, not only its health check.

## M6 — TLS at the edge

- [ ] Choose a DNS name (decision below), then Caddy certificates and redirection from
  HTTP to HTTPS.

**Done when** every public route is HTTPS-only with a valid certificate and HSTS.

## M7 — Secrets in Vault

- [x] The owner ran `vault operator init` on 2026-11-21 with one key share, and keeps the
  key and the root token on paper ([runbook](runbooks/vault-init.md)). The first init's
  output was pasted into a chat, so the empty Vault was reset and initialised again.
- [x] Vault published on the overlay (`10.44.0.1:8200`). Reachable from node2 and
  node3; the LAN-route test is blocked.
- [ ] An admin policy and identity, then revoke the initial root token.
- [ ] Back up Vault (Raft snapshot) and test the restore.
- [ ] Move the first consumers from ansible-vault to Vault.

**Done when** a service reads its credential from Vault, and Vault's own restore has
been drilled.

## M8 — Recovery you can trust

- [ ] Keep an off-site encrypted copy of the backups, with the backup key held offline
  (age) and its custody decided.
- [ ] Run a full restore drill of the current archive format into throwaway containers.
- [ ] Reboot node3 onto its pending kernel, after a controlled Redis failover (needs the
  owner's go-ahead; see the [runbook](runbooks/redis-failover-and-reboots.md)).
- [ ] Have the PostgreSQL dump use a `pg_read_all_data` role instead of the superuser.

**Done when** the whole platform can be restored from the off-site copy and the paper
password alone.

## The owner's physical tasks

- [ ] Remove the Windows installer USB stick from node2.
- [ ] Find out why every server links at 100 Mb/s (cables or switch).
- [ ] Set BIOS "power on after AC loss" on every machine; consider a UPS.
- [ ] Add DHCP reservations for every machine, including node4's Wi-Fi MAC.
- [ ] Use a Wi-Fi password longer than 8 characters, then rebuild the stick.

## Backlog (no milestone)

- **`ansible-lint` (26.11, 2026-11-21).** Apart from the deliberate skip in
  `.ansible-lint`, it reports:
  - 8 `risky-shell-pipe`: these need `set -o pipefail` and `executable: /bin/bash`,
    because dash on 22.04 lacks pipefail;
  - 3 `no-changed-when`;
  - 9 `name[casing]`;
  - 2 Jinja spacing.

  None of them hides a failure that a later assertion would not catch. Fix each with a
  live re-run of its role.
- **A pipeline** in Gitea Actions that runs the quality gates on every push. The runner
  exists; no pipeline does yet.

## Decisions pending (owner)

| Decision | Options | Blocks |
|---|---|---|
| A domain for TLS | a public name with Let's Encrypt (HTTP-01 needs port 80 forwarded; DNS-01 needs a DNS API); or Caddy's internal CA for WireGuard-only use | M6, Vaultwarden |
| LocalStack | free Hobby tier with an auth token (non-commercial use only); the last community image, pinned and frozen; or Moto, an open-source AWS mock | M5 LocalStack |
| Remote-access endpoint | node1, which is wired and always on (recommended); or node4 | M4 |
| Off-site backups | where they go (a cloud bucket or a second location) and who holds the key | M8 |

## Not doing

- **Microsoft SQL Server**: declined by the owner on 2026-11-20.
- **Jenkins**: Gitea Actions is used instead, because of RAM.
- **Kubernetes**: four 2-core machines are served well by Docker Compose.
- **Terraform**: nothing provider-managed exists yet. Revisit it with DNS automation.
- **MongoDB on node1–3**: those CPUs have no AVX.
- **A dedicated DevOps node**: there is no fifth machine.
