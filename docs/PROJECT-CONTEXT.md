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

Four facts from that table drive almost every decision:

1. **Every server disk is a spinning platter.** Disk I/O, not RAM or CPU, is the
   ceiling. The single highest-value upgrade available is one SATA SSD in whichever
   node holds the PostgreSQL primary.
2. **~3 GB of usable RAM per node.** Enough for two or three modest services, not for
   a full enterprise stack.
3. **Mixed OS versions, deliberately.** 22.04, 24.04 and 26.04 coexist. Nothing is
   reinstalled to align a version number.
4. **The laptop is not a server.** Wi-Fi, suspends, gets carried around, and shares
   RAM with unrelated work. It is a control plane, not a host.

Note `node1` holds **two DHCP leases on one NIC** (.101 and .23). This has caused
confusion before — it is one machine, not two.

---

## 3. Role assignment, and why each one landed where it did

| Node | Runs | Why here |
|---|---|---|
| **node1** | Caddy (public 80/443), Vault, SFTP primary, Redis Sentinel, RabbitMQ, backup job | The security boundary. Its disk is the least busy, which is why the broker and backups live here. |
| **node2** | PostgreSQL **primary**, Redis, Sentinel | Newest CPU of the three and a dedicated disk for write-heavy work. |
| **node3** | PostgreSQL standby, Redis, Sentinel, nginx app host, SQLite, Gitea + Actions runner, SFTP mirror | Twice the disk of the others (466 G) — repositories, application data and the mirror all need space. Read-mostly database load leaves headroom. |
| **laptop** | Ansible controller | Portable admin workstation. Nothing the platform depends on should run here. |

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

**Every database, broker, Vault and application socket binds its overlay address
only.** This is the primary security control, and the reason matters:

> **Docker publishes ports by writing to its own iptables chain, which is evaluated
> *before* ufw's INPUT chain. ufw will not protect a container port.** The bind
> address in the compose file is what keeps a service private — nothing else.

The `app_host` and `rabbitmq` roles assert their ports are not on `0.0.0.0` and fail
the play if they are.

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
re-basebackup. Promotion is a human decision.

### Redis 7 — replication with Sentinel

One master, one replica, three Sentinels at `quorum 2`, monitoring `homelab-redis`.

**The quorum arithmetic is the thing to internalise.** Three Sentinels at quorum 2
tolerate exactly one Sentinel loss. With two down, the survivor can never reach quorum
and **no failover is possible**. This is not a bug; it is the trade-off, and it was
observed in production (§8).

### Both halves were validated by a real outage

On 2026-11-18 node2 disappeared for 28 hours. Redis promoted node3 automatically and,
when node2 returned, Sentinel demoted it to a replica without intervention — textbook
behaviour, no split-brain. PostgreSQL was deliberately **not** promoted, so node2 came
back as the single primary and the standby resumed streaming on its own.

**Had PostgreSQL been promoted, recovery would have required `pg_rewind`.** Waiting was
the correct call and is the precedent to follow.

---

## 6. What was cut, and why

These appear in older revisions of the repository. They are **not** part of the
platform. Do not reintroduce them without revisiting the constraints in §2.

| Cut | Reason |
|---|---|
| **MongoDB** | WiredTiger's memory appetite on a 3.7 GB node; the 4.4 series is end-of-life. |
| **Microsoft SQL Server** | Developer/Standard needs 2 GB minimum, realistically 4+. Express would fit on node1 (it self-caps at ~1.4 GB) — **this was offered on 2026-11-20 and the user explicitly declined. Do not re-propose it.** |
| **Jenkins** | A JVM controller plus agents did not fit beside the data services. CI is Gitea Actions. |
| **RabbitMQ** | Cut originally, then **reinstated** on 2026-11-20 once Mongo, SQL Server and Jenkins were gone and ~3 GB per node was free. |
| **Dedicated DevOps node** | There is no fifth machine. |

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

### Deployed and verified

- WireGuard full mesh; all peers handshaking.
- PostgreSQL primary (node2) + streaming standby (node3), zero lag, slot active.
  Verified by writing on the primary and reading on the standby, which correctly
  refuses writes.
- Redis master (node3, post-failover) + replica (node2) + three agreeing Sentinels.
- Edge: Caddy serving and proxying to both Gitea and the app host.
- RabbitMQ 3.13.7, publish/consume verified.
- nginx app host + SQLite on node3.
- Gitea 1.22 + Actions runner on node3, runner daemon stable.
- SFTP mirror node1 → node3, verified including deletion propagation.
- Nightly AES-256 encrypted backups, **restore-drilled end to end**.
- Key-only SSH on all four machines, verified against effective config.
- Fail2Ban on all four, banning via ufw, LAN and overlays exempt.
- `ufw` default-deny everywhere; no database, broker or application port on `0.0.0.0`.

### Outstanding

| Item | Why it is not done |
|---|---|
| **Vault `operator init`** | Generates unseal keys and the root token. The agent is blocked from this by design; the user must run it and store the keys offline. |
| **Push 13 commits** | No GitHub credentials on the laptop: no credential helper, no `gh`, and the local `id_ed25519_github` key is rejected by GitHub. |
| **Laptop on the overlay** | `10.44.0.4` is reserved but no `wg1` interface exists. |
| **Router: forward 80/443 to node1, DHCP reservations by MAC** | Router changes are the user's manual step. Reservations matter — node1 has drifted across .101/.23. |
| **TLS at the edge** | Caddy listens on `:443` but no site is configured; `auto_https off` because there is no public DNS name yet. |
| **Monitoring and metrics** | Not started. |

### The single largest risk

**The laptop is a single point of failure for the entire platform.** These exist
nowhere else:

```
~/.ssh/homelab-controller     the only key with access to all three servers
~/.homelab_vault_pass         opens every ansible-vault secret AND the WireGuard backup
inventory/hosts.yml           git-ignored
inventory/group_vars/...      git-ignored, encrypted
13 unpushed commits
```

There is a circular trap here: the WireGuard configuration backup is encrypted with the
vault password that also lives only on the laptop.

An encrypted recovery bundle was prepared on 2026-11-20 containing all of the above,
sealed with a **separate** passphrase at `~/.homelab_recovery_pass` so it is not
self-referential. Distributing it was deliberately left to the user — moving private
keys between machines needs human sign-off.

---

## 10. Operating this repository

```bash
cd ansible
V=/home/vpetukhov/homelab/.venv/bin

$V/ansible-playbook playbooks/base.yml          # packages, sysctl
$V/ansible-playbook playbooks/overlay.yml       # WireGuard mesh
$V/ansible-playbook playbooks/security.yml      # ufw + sshd hardening (asserts sshd -T)
$V/ansible-playbook playbooks/fail2ban.yml
$V/ansible-playbook playbooks/data-primary.yml  # node2
$V/ansible-playbook playbooks/data-replica.yml  # node3
$V/ansible-playbook playbooks/sentinels.yml
$V/ansible-playbook playbooks/edge.yml          # node1
$V/ansible-playbook playbooks/sftp-mirror.yml
$V/ansible-playbook playbooks/app-host.yml      # node3
$V/ansible-playbook playbooks/rabbitmq.yml      # node1
$V/ansible-playbook playbooks/gitea.yml         # node3
$V/ansible-playbook playbooks/backup.yml        # last: snapshots what the others create
```

Secrets are `ansible-vault` files unlocked by `~/.homelab_vault_pass`, configured in
`ansible.cfg`. Never commit a decrypted one.

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
