# Roadmap

> Last updated **2026-11-24**. A box is ticked only when the result was verified on the
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
| node4 | reinstalled headless 2026-11-22: controller on Wi-Fi, firewall, Fail2Ban and `wg1` since 2026-11-24 | headless server on `wg1` (10.44.0.4): controller + MongoDB 8, Vaultwarden, LocalStack, monitoring |

**Why node4 gets the new services.** It has the only CPU with AVX, which MongoDB 5.0+
requires. It also has the only SSD and the most RAM (7.3 GB). CI stays on node3 so that
the password manager never shares a host with CI jobs.

**Planning estimate for node4's RAM, to be measured:**

| Service | RAM |
|---|---|
| MongoDB (WiredTiger cache capped at 1 GB) | ~1.5 GB |
| LocalStack | ~1 GB |
| HomeLab Pulse receiver and dashboard (budget; idle with one agent it took 0.09 GB on 2026-11-24) | ~0.7 GB |
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
  (`homelab-recovery-20261121-1821.tar.gpg`, rebuilt after the Vault work and the VM test). Verified: it opens with the new password
  (119 entries) and includes the GitHub deploy key and the laptop's `/etc/wireguard`.
  The old bundle was deleted.
- [ ] The owner keeps a copy of the bundle, with `RESTORE.txt`, somewhere other than the
  stick. node4 no longer has `~/homelab-recovery/`, so the stick may be the only copy.
- [x] The installer stick was rebuilt and verified on 2026-11-21. It carries Wi-Fi, SSH
  on 2345 and the bundle, with the three fixes found by the VM test. Its
  `homelab-restore` also fast-forwards the repository from GitHub, so commits pushed
  after the stick was made are not lost.
- [x] The stick keeps node4's address. The laptop's 192.168.0.35 was static in
  NetworkManager, not reserved on the router, and the router's 2345 forward points at
  it, so a DHCP install would have moved it. The Wi-Fi is now pinned to the same address,
  gateway and DNS servers (`--wifi-address`), checked with `netplan generate`. Written
  and read back: image sha256 `577920af…`.
- [x] The install and restore flow was tested end to end in a UEFI VM on 2026-11-21, with
  a variant of the stick's configuration: a direct kernel boot with the `autoinstall`
  argument, the identity set without prompts, a test sudoers drop-in and a test bundle
  with its own password. It predates the static Wi-Fi address. The VM had the disk
  serial set and no network, like the laptop without a cable. The test found three bugs
  before any hardware was wiped:
  - Wi-Fi in the live installer aborted the install (`iw` is missing);
  - `homelab-restore` could not read its own staged files as the user;
  - `homelab-restore` hit a stale apt index after the offline install.
  After the fixes:
  - install, restore and GitHub fast-forward all pass;
  - `ansible homelab -m ping` from the restored VM reaches node1–3;
  - the deploy key can push;
  - Claude Code installs.
- [x] The final image was booted on 2026-11-22 in a Secure Boot UEFI VM (OVMF with the
  Microsoft keys), read-only, with no network and a disk carrying the configured
  serial. It went through GRUB and the Profile configuration screen to the "Confirm
  destructive action" dialog (default No); nothing was written to the disk. Joining the
  Wi-Fi with the static address has not run on the real hardware yet.
- [x] Installed on 2026-11-22 (14:23 UTC), then `sudo homelab-restore` (14:25–14:30).
  Verified on 2026-11-23: Ubuntu 26.04.1, the Wi-Fi on 192.168.0.35, sshd on 2345 offering
  `publickey` only, suspend and the lid switch disabled.
- [x] SSH into node4 from outside, through the router. Verified on 2026-11-24 in sshd's
  journal: nine logins with the owner's workstation key from one public address between
  2026-11-22 and 2026-11-24. Unrelated Internet scanners reach sshd through the same
  forward.

**Done when:**

- from node4, `ansible homelab -m ping` passes with the pinned host keys and the vault
  opens (**passes**, 2026-11-23: all four machines, both vault files);
- the owner can still SSH in from outside (**passes**, 2026-11-24).

## M3 — node4 joins the platform

- [x] Add node4 to the mesh as `10.44.0.4` (2026-11-24). During the change the three
  server tunnels lost 0 of ~680 pings and did not restart; PostgreSQL lag 0, three
  Sentinels agreed. Sentinels and replication stay on the three servers.
- [x] Apply `security` and `fail2ban` to node4 (2026-11-24, with a rollback timer:
  [runbook](runbooks/firewall-with-rollback.md)). The `firewall` role reads the SSH port
  from `sshd -T`, and node4 has an explicit any-source exception for 2345. Verified: from
  node1, 2345 is open while a listener on another port is blocked; a failed login raises
  the jail's count; a new login from outside worked before the timer was cancelled; a
  second run changes nothing. The reboot below still has to prove persistence.
- [x] Apply `base` and `host_hardening` to node4 (2026-11-24): smartd (SSD PASSED),
  watchdog iTCO fed by systemd, panic sysctls. A second run changes nothing.
- [ ] Keep node4's SSH reachable from outside until M4 is done, then delete
  `inventory/group_vars/controller/firewall.yml`.
- [x] Docker on node4 (2026-11-24): 29.10.0, `live-restore`, json-file log rotation,
  ordered after `wg1` and the guard. The owner's account is not in the `docker` group.
- [x] Wi-Fi power saving off on node4 (2026-11-24, role `wifi_power_save`). Packets to an
  idle node4, 1 s apart: 37.5% lost from node1 and 42.5% from node2 before, 0 of 60 from
  each after. The reboot below also has to prove that it persists.
- [x] No LXD on node4 (2026-11-24, role `lxd_disabled`): the installer's first account is
  in the `lxd` group, which is root without a password as long as `lxd-installer.socket`
  listens. The group is empty and the socket masked; a second run changes nothing.
- [x] A become password for node4 (2026-11-24). The `ansible_become_pass` in
  `group_vars/homelab/vault.yml` is node1–3's and fails on node4 (verified 2026-11-23,
  with both `sudo-rs` 0.2.13, the 26.04 default, and the classic `sudo.ws`). node4's own
  is in `inventory/host_vars/node4-laptop/vault.yml` (ansible-vault, git-ignored); from
  node4, `ansible node4-laptop -b` becomes root.
- [x] The owner's terminal survives a dropped connection (2026-11-24): role
  `persistent_session`, plus the sshd keepalive (30 s) on node4, checked with `sshd -T`.
- [x] Make the `wireguard` role ready for a fourth peer that connects locally
  (2026-11-24): the endpoint no longer comes from `ansible_host`, the handler reloads
  instead of restarting, and the role asserts the live peers and pings each through `wg1`.
- [ ] Reboot node4 onto kernel -44 (a reboot has been pending since 2026-11-23). It also
  proves that `ufw`, Fail2Ban, `wg1` and the guard come back on their own.

**Done when:**

- node4 reaches every node over `wg1` (**passes**, 2026-11-24);
- the overlay guard is live on node4, and the LAN-route test is blocked there too
  (**passes**, 2026-11-24).

## M4 — Remote access over WireGuard

- [x] Decide where the endpoint lives (owner, 2026-11-24): on every node. A device peers
  with each node directly, so no node forwards traffic and no node is a single point of
  failure.
- [ ] Decide what happens to the laptop's legacy `wg0` (10.10.10.0/24).
- [ ] Add the owner's devices (Linux, macOS, iPhone). The role, the client script and the
  [runbook](runbooks/remote-access.md) are ready; the devices' public keys are missing.
- [ ] The owner adds four UDP forwards on the router, one per node.
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
  The role and the backup step are written and were tried on node4 with MongoDB 7.0 and
  throwaway passwords (2026-11-24): idempotent, authentication enforced, a dump, drop and
  restore returned 500 of 500 documents. Blocked by node4's kernel until the M3 reboot.
  No HA is claimed. WiredTiger cache capped; `mongodump` in the nightly backup.
- [ ] **LocalStack**: licensing decision first (see below).
- [ ] **Monitoring**: HomeLab Pulse (decided 2026-11-24, instead of Prometheus and
  Grafana): the agent on all four machines, the receiver and dashboard on node4. It is
  written and tested, its Compose stack has run on node4 as a test, and it is deployed
  nowhere. Nothing in it sends an alert yet, so done
  includes alerts for at least:
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
- [x] Run a full restore drill of the current archive format into throwaway containers
  (2026-11-24, `playbooks/restore-drill.yml`, from node3's copy): all eight parts restored
  and matched the live counts. Redis and Gitea are empty in production, so those two were
  also tried with synthetic data ([runbook](runbooks/backup-restore.md#drill-about-monthly-an-untested-backup-is-a-guess)).
- [x] The backup job on node1 no longer reports success without RabbitMQ (2026-11-24).
  The archive it wrote was restored by the drill the same evening, 8 of 8. The failing
  branch was exercised with a stubbed `docker`, not by stopping the live broker.
- [ ] Reboot node3 onto its pending kernel, after a controlled Redis failover (needs the
  owner's go-ahead; see the [runbook](runbooks/redis-failover-and-reboots.md)).
- [ ] Have the PostgreSQL dump use a `pg_read_all_data` role instead of the superuser.

**Done when** the whole platform can be restored from the off-site copy and the paper
password alone.

## The owner's tasks

They live in [owner-checklist.md](owner-checklist.md), in Bulgarian and with exact steps:
the first Gitea account, node4's reboot, the router forwards, WireGuard on the owner's
devices, and the physical tasks (the USB stick in node2, the 100 Mb/s links, BIOS
power-on after AC loss, DHCP reservations, a longer Wi-Fi password).

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
| Off-site backups | where they go (a cloud bucket or a second location) and who holds the key | M8 |
| Who sees the Pulse dashboard | WireGuard only, as the goal says (recommended until M6); or public behind Caddy with TLS, which its anonymous read endpoints are written for | M5 monitoring |

## Not doing

- **Microsoft SQL Server**: declined by the owner on 2026-11-20.
- **Prometheus and Grafana**: HomeLab Pulse replaces them (owner, 2026-11-24).
- **Jenkins**: Gitea Actions is used instead, because of RAM.
- **Kubernetes**: four 2-core machines are served well by Docker Compose.
- **Terraform**: nothing provider-managed exists yet. Revisit it with DNS automation.
- **MongoDB on node1–3**: those CPUs have no AVX.
- **A dedicated DevOps node**: there is no fifth machine.
