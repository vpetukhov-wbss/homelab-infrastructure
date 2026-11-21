# Project Context

> **Read this first.** It is the handover document for anyone — human or agent —
> picking this platform up without prior context. It records not just *what* exists
> but *why it is the way it is*, including the compromises, the decisions that were
> reversed, and the traps that cost real debugging time.
>
> Last verified against the running systems: **2026-11-21**.
>
> [`docs/architecture/overview.md`](architecture/overview.md) describes the topology.
> [`docs/security/posture.md`](security/posture.md) records verified security controls.
> This document explains the reasoning behind both.

---

## 1. What this is

A four-machine self-hosted platform built from second-hand desktop hardware, running
replicated PostgreSQL and Redis, a message broker, application hosting, self-hosted
git with CI, and encrypted backups — configured entirely through Ansible.

The repository is **public**. Architecture, automation and runbooks belong in it.
Credentials, private keys and real inventory do not, and are git-ignored.

**The goal** — one owner, these four machines, the services their applications need
(plus MongoDB, Vaultwarden and LocalStack on node4), reachable from anywhere by the owner
over WireGuard and by nobody else, encrypted backups with tested restores, and every
machine rebuildable from a USB stick and one password — and the milestones towards it
are in [`roadmap.md`](roadmap.md).

---

## 2. The hardware — this is the whole constraint

Everything in this platform is shaped by what these machines actually are. Read this
table before proposing anything.

| | `node1-gateway` | `node2-data-replica` | `node3-data` | laptop (`node4`) |
|---|---|---|---|---|
| Make | Lenovo 0385BE3 | Lenovo 0385BE3 | **Dell OptiPlex 755** | Lenovo ThinkPad L470 |
| CPU | Pentium G620 @ 2.60 GHz | Pentium G630 @ 2.70 GHz | Core 2 Duo E8400 @ 3.00 GHz | i5-6200U @ 2.30 GHz |
| Cores / threads | 2 / 2 | 2 / 2 | 2 / 2 | 2 / 4 |
| RAM | 3738 MB | 3742 MB | 3769 MB | 7304 MB |
| Disk | Seagate ST3250312AS 233 G **HDD** | WD WD2500AAKX 233 G **HDD** | WD WD5000AAKX 466 G **HDD** | Samsung MZ7LN256 238 G **SSD** |
| Root filesystem | 227 G | 227 G | 456 G | 233 G |
| OS | Ubuntu 22.04.5 | Ubuntu **24.04.5** | Ubuntu 22.04.5 | Ubuntu **26.04** |
| Kernel | 5.15.0-203 | 6.8.0-149 | 5.15.0-181 | 7.0.0-43 |
| NIC | `eno1`, wired | `eno1`, wired | `enp0s25`, wired | `wlp5s0` **Wi-Fi** (`enp0s31f6` unplugged) |
| LAN | 192.168.0.101 **and** .23 | 192.168.0.28 | 192.168.0.103 | 192.168.0.35 |
| Overlay | 10.44.0.1 | 10.44.0.2 | 10.44.0.3 | *(none — see §9)* |
| SSH port | **2222** | 22 | 22 | **2345** |

Two more measured facts, both found on 2026-11-21:

- **Every server NIC links at 100 Mb/s** although the NICs are gigabit — cabling or the
  switch/router port. Replication, backups and rsync are capped at ~11 MB/s.
- **node2 has no second data disk.** Its `sdb` is a forgotten Windows 11 installer USB
  stick — a boot-order hazard, not storage. Remove it before node2's next reboot.

Four facts from that table drive almost every decision:

1. **Every server disk is a spinning platter.** Disk I/O, not RAM or CPU, is the
   ceiling. The single highest-value upgrade available is one SATA SSD in whichever
   node holds the PostgreSQL primary.
2. **~3 GB of usable RAM per node.** Enough for two or three modest services, not for
   a full enterprise stack.
3. **Mixed OS versions, deliberately.** 22.04, 24.04 and 26.04 coexist. Nothing is
   reinstalled to align a version number.
4. **The laptop was not a server — that decision was reversed on 2026-11-21.** It is
   being reinstalled headless as `node4` (Ubuntu Server 26.04.1, Wi-Fi, suspend and lid
   switch disabled) and will be both a server and the controller. It is the **only
   machine with AVX**, which MongoDB 5.0+ requires; the Pentium G620/G630 and the Core 2
   Duo do not have it. See `installer/README.md`.

Note `node1` holds **two DHCP leases on one NIC** (.101 and .23). This has caused
confusion before — it is one machine, not two.

---

## 3. Role assignment, and why each one landed where it did

| Node | Runs | Why here |
|---|---|---|
| **node1** | Caddy (public 80/443), Vault, SFTP primary, Redis Sentinel, RabbitMQ, backup job | The security boundary. Its disk is the least busy, which is why the broker and backups live here. |
| **node2** | PostgreSQL **primary**, Redis, Sentinel | Newest CPU of the three. (It does *not* have a second data disk — see §2.) |
| **node3** | PostgreSQL standby, Redis, Sentinel, nginx app host, SQLite, Gitea + Actions runner, SFTP mirror | Twice the disk of the others (466 G) — repositories, application data and the mirror all need space. Read-mostly database load leaves headroom. |
| **laptop → node4** | Ansible controller today. Planned: MongoDB 8 (single-node replica set, no HA), LocalStack, Vaultwarden, monitoring | Only AVX CPU, only SSD, most RAM (7.3 GB). CI stays on node3 so the password manager and MongoDB never share a host with CI. |

### Placement decisions worth understanding

**Applications are on node3, not node1.** node1 terminates public traffic *and* holds
Vault. Application code there would already be inside the security boundary if
compromised. node2 was rejected because it is the write primary on a single spindle.

**RabbitMQ is on node1, not a data node.** A broker with persistent queues competing
with PostgreSQL for the same platter is the worst available placement. node1's disk is
comparatively idle.

**Gitea moved from the laptop to node3.** Originally planned for the laptop's SSD. The
laptop had 591 MB free with its other work, and the blocker that stalled the move (the
user not being in the `docker` group) was really a symptom: a laptop is the wrong place
for a service.

**Sentinels run on the three wired nodes only.** A Wi-Fi Sentinel produces false
failure detections. Quorum stability matters more than an extra vote.

---

## 4. Network design

### Underlay — treat the LAN as hostile

Packet capture on the wired segment shows ARP traffic from **other subscribers' CPE
devices** in the ISP's `10.209.0.0/16` range, leaking through the cable modem. The LAN
is semi-public. No control in this platform may rest on "only our devices are here".

### Overlay — WireGuard `wg1`, 10.44.0.0/24

A **full mesh** between node1, node2 and node3, not hub-and-spoke, so any one node can
fail without partitioning the other two. Each peer carries a single `/32` `AllowedIPs`.

Every database, broker, Vault and application port is published on the overlay
address. **That binding is necessary but NOT sufficient**, and this was wrong in this
document until 2026-11-21:

> **Docker publishes ports with DNAT rules that match the destination address on ANY
> ingress interface, and that traffic never reaches ufw's INPUT chain.** A LAN host that
> adds `ip route add 10.44.0.0/24 via <node LAN IP>` reaches every "overlay-only" port.
> Verified: 14 of 14 ports were open that way.

**The control is the overlay guard** (firewall role, `homelab-overlay-guard.service`): a
`raw`-table chain, evaluated before NAT, that drops every packet for `10.44.0.0/24`
unless it arrived on `wg1`, loopback or a Docker bridge. Re-tested: 14 of 14 blocked.
The bind address still matters — it keeps ports off `0.0.0.0` — and the `app_host` and
`rabbitmq` roles still assert it.

**The overlay range collides with the ISP.** `10.44.0.1` answers from about six hops
inside the ISP's network. Without `wg1`, traffic for the overlay would follow the
default route there (Redis `AUTH` is clear text). The guard unit therefore also
installs `unreachable 10.44.0.0/24 metric 4000`; `wg1`'s route wins whenever it is up.
A machine that is not on the overlay (today: the laptop) must not talk to `10.44.0.x`.

### Edge

node1 is the only machine listening publicly on anything but SSH. Caddy serves `:80`
and reverse-proxies over the overlay:

```
Internet → router → node1:80/443 (Caddy)
                      ├── /git/*  → 10.44.0.3:3000 (Gitea)
                      └── /*      → 10.44.0.3:8080 (nginx → app containers)
```

**No router port-forward to node1 exists yet**, so this is LAN-reachable only. The one
genuinely Internet-facing port today is SSH on the laptop (2345).

---

## 5. Data layer and failover semantics

### PostgreSQL 16.16 — asynchronous streaming replication

Primary on node2, standby on node3, bootstrapped with `pg_basebackup`, held by
replication slot `node3_slot`. Asynchronous is deliberate: synchronous commit on
spinning disks makes every write wait on the slower of two platters.

**There is no automatic PostgreSQL failover, by design.** Promoting while the old
primary is merely *unreachable* produces two primaries and forces `pg_rewind` or a full
re-basebackup. Promotion is a human decision — `docs/runbooks/postgres-promotion.md`.

Until 2026-11-21 a promotion could not actually have worked: the standby ran on the
image's default `pg_hba` (local connections only), so neither the applications nor
node2 could have connected to it, and with `wal_log_hints` off `pg_rewind` refuses to
run. Both nodes now have a templated `pg_hba`, `wal_log_hints = on`, and the
replication slot is capped (`max_slot_wal_keep_size = 20GB`) so a standby that stays
down for days cannot fill the primary's only disk.

### Redis 7 — replication with Sentinel

One master, one replica, three Sentinels at `quorum 2`, monitoring `homelab-redis`.

**The quorum arithmetic is the thing to internalise.** Three Sentinels at quorum 2
tolerate exactly one Sentinel loss. With two down, the survivor can never reach quorum
and **no failover is possible**. This is not a bug; it is the trade-off, and it was
observed in production (§8).

**Failover state must survive restarts, and until 2026-11-21 it did not.** Sentinel
persists the current master by writing a temp file and renaming it over its config;
on a single-file bind mount that rename fails (EBUSY), so every Sentinel still had
`monitor … node2` on disk while node3 was master. Redis configs were read-only files
with a hard-coded `replicaof`. A simultaneous Sentinel restart would have followed the
wrong master. Now: config *directories*, seeded once from the live topology (role
`redis_topology`) and owned by Redis/Sentinel afterwards; Sentinel requires a password;
`down-after-milliseconds` is 20 s. Controlled failover and rolling reboots:
`docs/runbooks/redis-failover-and-reboots.md`.

### Both halves were validated by a real outage

On 2026-11-18 node2 disappeared for 28 hours. Redis promoted node3 automatically and,
when node2 returned, Sentinel demoted it to a replica without intervention — textbook
behaviour, no split-brain. PostgreSQL was deliberately **not** promoted, so node2 came
back as the single primary and the standby resumed streaming on its own.

**Had PostgreSQL been promoted, recovery would have required a full re-basebackup** —
`pg_rewind` was impossible at the time (see above), and the promoted standby would not
even have accepted application connections. Waiting was the correct call and is the
precedent to follow.

---

## 6. What was cut, and why

These appear in older revisions of the repository. They are **not** part of the
platform. Do not reintroduce them without revisiting the constraints in §2.

| Cut | Reason |
|---|---|
| **MongoDB** | WiredTiger's memory appetite on a 3.7 GB node; the 4.4 series is end-of-life; and **none of the three servers has AVX**, which MongoDB 5.0+ requires. Planned to return on node4 only (§3). |
| **Microsoft SQL Server** | Developer/Standard needs 2 GB minimum, realistically 4+. Express would fit on node1 (it self-caps at ~1.4 GB) — **this was offered on 2026-11-20 and the user explicitly declined. Do not re-propose it.** |
| **Jenkins** | A JVM controller plus agents did not fit beside the data services. CI is Gitea Actions. |
| **RabbitMQ** | Cut originally, then **reinstated** on 2026-11-20 once Mongo, SQL Server and Jenkins were gone and ~3 GB per node was free. |
| **Dedicated DevOps node** | There is no fifth machine. |
| **Terraform** | Planned for DNS/Cloudflare and Vault configuration; nothing provider-managed was ever created, so the empty `terraform/` directory was removed on 2026-11-21. Revisit only together with a real resource (e.g. DNS automation for TLS). |

The general principle: **containers solve version and dependency isolation. They do
not create RAM, cores or disk throughput.** Two databases on one spindle are slow
whether or not they are containerised.

---

## 7. Traps discovered the hard way

Each of these produced a system that *reported success while achieving nothing*. They
are the most valuable part of this document.

### sshd honours the FIRST value it reads

`Include /etc/ssh/sshd_config.d/*.conf` expands **alphabetically**, and sshd takes the
first value for a keyword. Ubuntu cloud images ship `50-cloud-init.conf` containing
`PasswordAuthentication yes`, which **beat** a hardening file named `99-*`. node1 and
node2 accepted SSH passwords for days while the Ansible role reported success on every
run. The drop-in is now `00-homelab-hardening.conf`.

Worse, the role validated with `sshd -t`, which checks **syntax** and cannot detect an
overridden directive. It now asserts against `sshd -T`.

> **Verify the effective state, never the configuration you wrote. A green role run
> proves only that the role ran.**

### Editing a bind-mounted config file is not deployment

`admin off` disables Caddy's admin API, so `caddy reload` fails with connection refused
on :2019. And `docker compose up -d` does **not** recreate a container when only a
bind-mounted file changed. The new Caddyfile sat on disk while Caddy served the old
config from memory. The `edge` role now restarts Caddy from a handler.

### lsyncd fails silently

lsyncd runs as root and the ssh options it hands to rsync do not reliably carry
`StrictHostKeyChecking`. Every sync died with `Host key verification failed` and rsync
exit 255 while the playbook reported success. The role now seeds
`/root/.ssh/known_hosts` with `ssh-keyscan` before lsyncd starts.

> **Check `/var/log/lsyncd/lsyncd.log`, not the play recap.**

### The act_runner image ignores CLI flags

Its entrypoint is `/sbin/tini -- run.sh`, a wrapper that configures itself from
**environment variables**. Passing `--instance` to it yields `instance address is empty`
and an endless retry loop. Registration must use `--entrypoint act_runner`.

Separately, `register` writes its `.runner` credential into the **current working
directory**, not `/data`. Without `-w /data` the step reports changed, writes nothing
the daemon can find, and act_runner loops on `token is empty`.

### Gitea refuses to run as root

`docker exec gitea gitea …` lands as root and aborts. The CLI must drop with `-u git`.

### Ubuntu's installer under-allocates LVM

subiquity creates a ~100 GB logical volume and leaves the rest of the volume group
unallocated. **All three servers** were affected — node3 was using 100 G of a 466 G
disk. The user suspected a failed install and asked for a reinstall; a reinstall would
have reproduced the same layout and destroyed the replicas.

Fixed online, no downtime, no reboot:

```bash
sudo lvextend -l +100%FREE /dev/ubuntu-vg/ubuntu-lv
sudo resize2fs /dev/mapper/ubuntu--vg-ubuntu--lv
```

> **Check `vgs` for free extents before accepting "the disk is too small".**

### Binding a container port to the overlay IP did not keep it off the LAN

The platform's "primary control" was never tested along the attacker's path: a LAN
route to the overlay address. 14 of 14 ports answered. See §4 and
`docs/security/posture.md`. **Test the path an attacker would use, not the one you
configured.**

### A single-file bind mount breaks anything that replaces its config file

Both Sentinel (persisting failover state) and Ansible's `template` write a temp file and
rename it into place. On a single-file mount the rename fails (Sentinel: EBUSY, state
lost on restart) or lands on a new inode the container never sees (PostgreSQL
`pg_reload_conf()` re-read the stale file). **Mount directories.**

### A container cannot reach its host's published port via the host's own IP

act_runner pointed at `http://10.44.0.3:3000` from the same compose bridge as Gitea.
Docker does not DNAT same-bridge traffic, so the packet reached INPUT and ufw dropped
it: 693 crash-loop restarts while this document said "runner daemon stable". Use the
compose service name (`http://gitea:3000`).

### systemd-modules-load honours Ubuntu's watchdog blacklist

`/etc/modules-load.d/iTCO_wdt.conf` loads nothing at boot, silently — found only by
actually rebooting node1. The driver is loaded by an explicit `modprobe` in
`homelab-watchdog-module.service`; check `/sys/class/watchdog/watchdog0/state` is
`active`. node3's Dell BIOS locks the TCO timer, so node3 uses `softdog`.

### rrsync refuses what lsyncd sends by default

lsyncd passes `-s` (`--protect-args`), which rrsync rejects ("option -s has been
disabled"), and `default.rsyncssh` runs plain `ssh mv/rm`, which a forced command
refuses. Use `default.rsync` with `protect_args = false`.

### Smaller ones

- **`${#array[@]}` in a `.j2` bash template opens a Jinja comment** (`{#`): "Missing end
  of comment tag". Count with a variable instead.
- **ControlPersist hides host-key checking.** A deliberately wrong pinned key "passed"
  because Ansible reused an already-verified SSH master; test with `ControlMaster=no`.
- **netplan/networkd cannot match Wi-Fi by glob** (name the interface), and the Ubuntu
  Server image does not ship `wpasupplicant` (it is in the ISO's offline pool).
- **`gitea dump` aborts on an instance with no repositories yet**
  (`open /data/git/repositories`); create the directory first.

### Toolchain mechanics

- **Run ansible from `ansible/`.** `ansible.cfg` points at `../inventory/hosts.yml`;
  the real inventory lives at the **repository root**, not under `ansible/`.
- **Ansible ad-hoc `-a` chokes on Docker Go templates** (`{{.Names}}`) because Jinja
  parses them first. Use `docker ps | awk` instead.
- **The ansible shell module uses `/bin/sh` (dash)**, not bash — no `${var:0:8}`.
- **`ansible-vault` needs blocking stdio**; redirect its output to a file. And
  `ansible-vault view` corrupts binary payloads — use `decrypt --output=FILE`.
- **YAML folded scalars (`>`) preserve newlines** on more-indented continuation lines.
  This split an `ssh-keyscan` host argument onto its own line and broke it. Use a
  literal block (`|`) for multi-line shell.
- **`$(cmd)` inside a double-quoted `ssh "…"` runs locally**, not on the remote host.

---

## 8. Incident history

### 2026-11-15 to 11-19 — two nodes lost, four days apart

node1 vanished on 11-15 at 21:10 local; node2 on 11-18 at 07:21. Both disappeared
instantly and completely from layer 2 with no graceful-shutdown signature. Neither
responded to ICMP, ARP, IPv6 multicast, passive `tcpdump`, or Wake-on-LAN. Both
returned only when physically powered on.

What this cost, and what it taught:

- **With two Sentinels down, Redis auto-failover was impossible.** Exactly the
  documented quorum trade-off, observed live.
- **For 28 hours the platform had no writable database.** The standby is read-only and
  PostgreSQL does not self-promote.
- **`arp-scan --localnet` run from a *wired* node is the definitive presence test** —
  it works at ARP level and firewalls cannot hide a host from it. ICMP sweeps from the
  Wi-Fi laptop were consistent but less convincing.

Both journals simply stop (no shutdown, no OOM, no MCE): a hard hang or a power loss.
Since 2026-11-21 a hang ends in a reboot instead of days of absence — hardware watchdog
(iTCO on node1/node2, `softdog` on node3) fed by systemd every 30 s, plus
`kernel.panic=10` and soft/hard-lockup panics. Still manual: BIOS *power on after AC
loss* on each machine, and a UPS.

### Reversible decisions taken under pressure

While node1 was down, the edge role was **temporarily relocated to node3** so the build
would not be hostage to one machine. When node1 returned, its own stack auto-restarted
(`restart: unless-stopped`), producing two edge stacks. The duplicate was torn down and
the relocation reverted.

> Nothing was lost because that Vault was never initialised and the SFTP tree was
> empty. Had either held state, the rollback would have been destructive. Temporary
> relocations of stateful roles need an explicit teardown plan.

---

## 9. Current state

### Deployed and verified (2026-11-21, against the running systems)

- WireGuard full mesh; all peers handshaking. **Overlay guard** live on all three
  nodes; the LAN-route attack re-tested: 14 of 14 overlay ports blocked.
- PostgreSQL 16.16 primary (node2) + streaming standby (node3), zero lag. Write on the
  primary seen on the standby, which refuses writes. `wal_log_hints` on, slot capped at
  20 GB (`wal_status = reserved`), standby has its own `pg_hba`.
- Redis 7.4.12 master (node3) + replica (node2); roles persisted on disk; three
  Sentinels with authentication, quorum OK, state persisted (`myid`, peers, epoch 7).
- Edge: Caddy proxying to Gitea and the app host, with security headers.
- Vault 1.18.5 on loopback, uninitialised.
- RabbitMQ 3.13.7. nginx app host + SQLite on node3.
- Gitea 1.22.6 + act_runner 0.6.1: runner declared to Gitea, stable (0 restarts), jobs
  without the Docker socket and resource-capped. No job has been run as a test yet.
- SFTP mirror node1 → node3 through the rrsync-confined `homelab-sync` account; shell
  access with the key refused.
- Nightly encrypted backups: PostgreSQL (+ roles), Redis, RabbitMQ definitions, SQLite,
  **Gitea dump**, **SFTP tree**, **node1's WireGuard config**; verified, then **copied to
  node3**. A full restore drill of the new archive format has not been done yet.
- Key-only SSH, X11/agent forwarding off, SSH accepted from the LAN subnet and `wg1` only.
  Ansible verifies host keys.
- Fail2Ban on all four; only loopback, overlay and the controller are exempt.
- smartd on every server (all three HDDs PASSED, 0 reallocated/pending sectors);
  watchdog active on all three; Docker log rotation and `live-restore`; Docker starts
  after `wg1`. Every image pinned (`inventory/group_vars/all/images.yml`).
- node1 rebooted onto kernel -204 and came back on its own with everything above intact.

### Outstanding

| Item | Why it is not done |
|---|---|
| **Vault `operator init`** | Generates unseal keys and the root token; the user runs it and stores the keys offline (`docs/runbooks/vault-init.md`). |
| **node3 reboot (kernel -181 → -204, libc)** | Needs a controlled Redis failover first; failovers need the user's go-ahead. Runbook ready. |
| **Pull the Windows USB stick out of node2** | Physical. node2 is on its newest kernel, so no reboot is pending. |
| **100 Mb/s links, BIOS power-on-after-AC-loss** | Physical/BIOS. |
| **Laptop reinstall as node4** | Installer stick built (Wi-Fi + recovery restore + SSH kept on 2345 so the router's forward — the owner's only remote path — keeps working); waits for the user's new homelab password, after which the bundle and the stick are rebuilt. |
| **Push to GitHub** | Committed on 2026-11-21 (17 commits ahead of `origin/main`). The push needs a GitHub credential on the controller — a write deploy key for this repository. The recovery bundle carries the full repository meanwhile. |
| **TLS, router forwards, remote access (WireGuard road warrior)** | Need a DNS name and a decision on which `wg0` to keep. |
| **Backup key offline (`age`), off-site copy, restore drill** | Design decision on where the key and the off-site copy live. |
| **Monitoring and metrics** | Planned on node4. |

### The single largest risk

**The laptop is a single point of failure for the entire platform.** These exist
nowhere else:

```
~/.ssh/homelab-controller     the only key with access to all three servers
~/.homelab_vault_pass         opens every ansible-vault secret AND the WireGuard backup
inventory/hosts.yml           git-ignored
inventory/group_vars/...      git-ignored, encrypted
17 unpushed commits (until the first push)
```

There is a circular trap here: the WireGuard configuration backup is encrypted with the
vault password that also lives only on the laptop.

The 2026-11-20 recovery bundle was written to `/tmp`, which is tmpfs on the laptop: it
vanished with the next reboot. **Never put anything that must survive in `/tmp`.** The
bundle is now produced by `scripts/make-recovery-bundle.sh` into `~/homelab-recovery/`,
sealed with the homelab password (`scripts/set-homelab-password.sh` lets the user pick
one they can remember), and the node4 installer stick carries it together with
`homelab-restore`. Copying it off the laptop is still the user's step.

---

## 10. Operating this repository

```bash
cd ansible
V=/home/vpetukhov/homelab/.venv/bin

$V/ansible-playbook playbooks/site.yml          # every layer below, in this order
$V/ansible-playbook playbooks/base.yml          # packages, sysctl, smartd, watchdog, Docker daemon
$V/ansible-playbook playbooks/overlay.yml       # WireGuard mesh
$V/ansible-playbook playbooks/security.yml      # ufw + overlay guard + sshd (asserts sshd -T, guard live)
$V/ansible-playbook playbooks/fail2ban.yml
$V/ansible-playbook playbooks/data-primary.yml  # node2  (discovers the live Redis master first)
$V/ansible-playbook playbooks/data-replica.yml  # node3  (same)
$V/ansible-playbook playbooks/sentinels.yml     # one Sentinel at a time, then a quorum check
$V/ansible-playbook playbooks/edge.yml          # node1
$V/ansible-playbook playbooks/app-host.yml      # node3
$V/ansible-playbook playbooks/rabbitmq.yml      # node1
$V/ansible-playbook playbooks/gitea.yml         # node3
$V/ansible-playbook playbooks/sftp-mirror.yml   # node3 sync account + exports, then node1 mirror
$V/ansible-playbook playbooks/backup.yml        # last: snapshots what the others create
```

On a machine built from scratch the order matters: node3's exports snapshot Gitea and
the app host's SQLite, so `sftp-mirror.yml` (which installs them) runs after both.
Templates for the git-ignored files: `inventory/hosts.example.yml`,
`inventory/secrets.example.yml`.

Secrets are `ansible-vault` files unlocked by `~/.homelab_vault_pass`, configured in
`ansible.cfg`. Never commit a decrypted one. Image versions live in
`inventory/group_vars/all/images.yml`; host keys are pinned in `~/.ssh/known_hosts`
under the inventory names.

Runbooks: `docs/runbooks/` — Vault init, Redis failover and rolling reboots, PostgreSQL
promotion, restoring a backup, controller recovery.

Scripts: `scripts/set-homelab-password.sh` (re-key the vault to a password you choose),
`scripts/make-recovery-bundle.sh` (encrypted controller bundle),
`scripts/build-installer-iso.py` + `scripts/write-installer-usb.sh` (node installer).

### Conventions worth keeping

- **Discover roles at runtime, never hardcode them.** The backup job probes for
  `pg_is_in_recovery() = f` and asks Sentinel for the Redis master, because failover
  moves both. A hardcoded host would silently dump a read-only standby.
- **Every role that exposes a port asserts it is not on `0.0.0.0`.**
- **Every safeguard verifies itself.** The backup decrypts and lists its own archive
  and deletes it on failure; the SFTP mirror writes a canary and confirms arrival.
- **Host and container names lie.** `node2-data-replica` holds the PostgreSQL
  *primary*; node3's container named `redis-replica` is the Redis *master*. Query the
  service, never the name.
- **Never re-template state a service owns.** Redis and Sentinel configs are seeded once
  and then belong to Redis/Sentinel; tunables go through `CONFIG SET`/`SENTINEL SET`.
- **Reboot to prove it.** A setting that only exists in the running system (a module
  loaded by hand, an iptables rule) is gone at the next boot. node1's reboot is what
  exposed the watchdog driver never loading.
