# Architecture Overview

> **Status:** describes the platform **as actually deployed** on 2026-11-19, on the
> four machines that exist. Anything not deployed is listed under
> [Planned](#planned-not-deployed) or [Explicitly cut](#explicitly-cut) and is not
> implied anywhere else in this document.

## Design intent

The lab is modelled as a small platform with explicit edge, data and control-plane
responsibilities. Logical roles are the stable abstraction; physical hosts are
replaceable implementation details.

Two constraints shape every decision here, and both are hardware-imposed:

- **Four machines, no more.** Three repurposed ThinkCentres (2-core, ~3.7 GB RAM,
  spinning disks) plus one laptop. Roles are packed onto these four, not spread
  across an idealised topology.
- **Disk I/O is the ceiling.** The ThinkCentres have spinning HDDs. The single
  highest-value hardware upgrade would be one SATA SSD in the PostgreSQL primary.

## The four machines

| Host | LAN | Overlay | OS | Role |
|---|---|---|---|---|
| `node1-gateway` | 192.168.0.101 (also .23) | 10.44.0.1 | Ubuntu 22.04.5 | Edge: Caddy, Vault, SFTP primary, Sentinel, RabbitMQ, backup job |
| `node2-data-replica` | 192.168.0.28 | 10.44.0.2 | Ubuntu 24.04 | PostgreSQL **primary**, Redis, Sentinel |
| `node3-data` | 192.168.0.103 | 10.44.0.3 | Ubuntu 22.04.5 | PostgreSQL standby, Redis, SFTP mirror, Sentinel, **app host** (nginx + SQLite) |
| laptop (`node4`) | 192.168.0.35 | *(not on overlay)* | — | Ansible controller |

**Host and container names do not reliably indicate current role.** After a Redis
failover the names invert: `node2-data-replica` holds the PostgreSQL *primary*, and
node3's container named `redis-replica` may be the Redis *master*. Always query the
service, never the name:

~~~bash
# PostgreSQL: which is primary?   (f = primary, t = standby)
docker exec postgres-primary psql -U postgres -At -c "select pg_is_in_recovery();"

# Redis: which is master?
docker exec <container> redis-cli -a "$PW" info replication | grep '^role'
~~~

SSH ports differ per host: node1 listens on **2222**, node2 and node3 on **22**, the
laptop on **2345**.

## Network layers

### Underlay

Switched Ethernet. node1, node2 and node3 are wired; the laptop is on Wi-Fi. The LAN
is **not** a trusted boundary — the cable modem leaks other subscribers' ARP traffic
onto this segment (CPE devices in the ISP's `10.209.0.0/16` range have been observed).
Nothing in this platform may depend on the LAN being private.

### Overlay

WireGuard `wg1`, `10.44.0.0/24`, a **full mesh** between node1, node2 and node3 —
not hub-and-spoke, so any one node can fail without partitioning the other two.
Each peer carries a single `/32` `AllowedIPs`. Every database, Vault and SFTP socket
binds to the overlay address only.

The laptop is in the inventory as `10.44.0.4` but **has no `wg1` interface yet**; it
reaches the fleet over the LAN via SSH. It also runs an unrelated legacy `wg0`
(`10.10.10.0/24`) which is not part of this platform.

### Edge

`node1-gateway` is the only machine that listens on a public port other than SSH.
Caddy serves `:80` and `:443`. Everything else — databases, Vault, Sentinel, SFTP —
is overlay-only and is never reachable from the LAN, let alone the Internet.

## Data resiliency

### PostgreSQL 16 — asynchronous streaming replication

Primary on node2, standby on node3, bootstrapped with `pg_basebackup` and held by
replication slot `node3_slot`. Asynchronous was chosen deliberately: synchronous
commit on spinning disks would make every write wait on the slower of two HDDs.

**There is no automatic PostgreSQL failover.** Promotion is a deliberate manual
operation, because promoting while the old primary is merely unreachable creates two
primaries and forces a `pg_rewind` or a full re-basebackup to recover.

### Redis 7 — replication with Sentinel

One master, one replica, and **three Sentinels** (node1, node2, node3) with
`quorum 2`, monitoring a master named `homelab-redis`.

Sentinels run on the three wired nodes only. The laptop is excluded on purpose: a
Wi-Fi Sentinel introduces false failure detections, and quorum stability matters more
than an extra vote.

**Quorum arithmetic is the thing to understand here.** Three Sentinels with quorum 2
tolerate exactly one Sentinel loss. With two Sentinels down, the survivor cannot reach
quorum and **no failover can occur** — this is not a bug, it is the designed trade-off,
and it was observed in practice on 2026-11-18 when two nodes were offline
simultaneously.

### SFTP mirror

`lsyncd` on node1 watches the SFTP tree and mirrors it to node3 over the overlay via
`rsync`-over-SSH, using a dedicated ed25519 key restricted in node3's
`authorized_keys` to node1's overlay address with no pty or forwarding.

Deletions propagate while lsyncd is running (`delete = "running"`), but it never
performs a bulk delete at startup, so an empty or unmounted source cannot wipe the
mirror. Ownership is **not** mirrored — rsync runs unprivileged on the target, so
promoting the mirror requires `chown -R 1001:1001` on the target directory.

This is a mirror, not a backup: it faithfully replicates deletions and corruption.

## Application hosting

Applications run on **node3**, behind nginx bound to the overlay address only. The
only path in is Caddy on the edge:

~~~
Internet → router → node1:80/443 (Caddy, the only public listener)
                       ↓ overlay
                    node3:8080 (nginx) → application containers
~~~

node3 was chosen by elimination. node1 terminates public traffic and holds Vault,
which makes it the worst possible neighbour for application code — a compromised app
there would already be inside the security boundary. node2 is the PostgreSQL write
primary, and the single spinning disk is already the platform's ceiling. node3 has
the largest disk and a read-mostly workload.

Vhosts go in `stacks/apphost/sites/`, static files in `stacks/apphost/www/`.
Application containers join the shared `apphost` Docker network and are reachable by
name from nginx. **Applications must never publish on `0.0.0.0`** — Docker writes its
port rules ahead of ufw's INPUT chain, so the bind address, not the firewall, is what
keeps them off the LAN. The role asserts this for nginx itself.

### SQLite

SQLite databases live in `stacks/apphost/sqlite/`. They are backed up with
`sqlite3 .backup` rather than a file copy: copying a database while it is being
written captures a torn page, whereas the backup API is consistent under concurrent
writes.

## Messaging

RabbitMQ 3.13 runs on **node1**, with AMQP on `10.44.0.1:5672` and the management UI
on `:15672` — both overlay-only. It sits on the edge node precisely because it is the
one node whose disk is comparatively idle; a broker with persistent queues competing
with PostgreSQL for the same spindle would be the worst placement available.

Its memory high-watermark is set as an **absolute** value rather than a fraction, so
its appetite cannot grow if a node is ever given more RAM, and a disk-free limit
blocks publishers before a filling disk takes PostgreSQL down with it.

## Backups

Nightly, on a systemd timer, stored on **node1** — a third machine, so losing either
data node does not take the backups with it.

The job covers `pg_dump` of the PostgreSQL primary, a Redis RDB snapshot, RabbitMQ
definitions and every SQLite database on the app host. Archives are **AES-256
encrypted at rest**, because none of these disks are encrypted and the hardware is
second-hand. Retention is 21 archives.

Two properties matter more than the schedule:

- **Roles are discovered at runtime, never assumed.** Sentinel moves the Redis master
  and a promotion moves the PostgreSQL primary, so a hardcoded host would silently
  start dumping a read-only standby. The first run found the Redis master on node3,
  where the 2026-11-18 failover had left it.
- **Every run verifies its own archive** by decrypting and listing it, and deletes it
  on failure. An unverified backup is a guess.

Restore has been drilled end to end: a seeded row was dumped, encrypted, decrypted,
restored into a throwaway database and read back; the SQLite canary likewise came back
with `integrity_check ok`.

Note that neither the PostgreSQL standby nor the SFTP mirror is a backup — both
propagate `DROP TABLE` and corruption within milliseconds. Only these archives do.

## Failover: PostgreSQL is manual, Redis is automatic

| | PostgreSQL | Redis |
|---|---|---|
| Detection | manual | Sentinel, `down-after 5s` |
| Promotion | manual (`pg_promote()`) | automatic, needs quorum 2 of 3 |
| Old primary rejoining | manual `pg_rewind` or re-basebackup | automatic demotion to replica |

Both halves of this were exercised unintentionally on 2026-11-18 when node2 was lost
for 28 hours. Redis promoted node3 and, on node2's return, demoted it to a replica
without intervention. PostgreSQL was deliberately **not** promoted, so when node2
returned it was still the single primary and the standby resumed streaming on its own.
Had it been promoted, recovery would have required `pg_rewind`.

## Control plane

Ansible configures machines and services from the laptop; roles live in
[`ansible/`](../../ansible/README.md) and are driven by inventory and group variables
rather than per-machine special cases. Secrets are held in `ansible-vault` files that
are git-ignored, unlocked by a password file outside the repository.

HashiCorp Vault 1.18 runs on node1 with Raft storage behind self-signed TLS on the
overlay. It is **deployed but not initialised** — see [Planned](#planned-not-deployed).

## Planned, not deployed

- **Vault initialisation.** `vault operator init` has not been run; Vault is sealed
  and uninitialised and holds no secrets. It cannot be an operational dependency until
  the unseal keys and root token are generated and stored offline.
- **Gitea and Gitea Actions** on the laptop. The role is written but undeployed: the
  laptop's user is not in the `docker` group.
- **Laptop on the overlay.** No `wg1` interface, so `10.44.0.4` is reserved, not live.
- **TLS at the edge.** Caddy listens on `:443` but no TLS site is configured; the
  Caddyfile sets `auto_https off` and defines only a `:80` block.
- **Public ingress.** No router port-forward to node1 exists, so `:80`/`:443` are
  reachable on the LAN only.
- **Monitoring and metrics.**

## Explicitly cut

These appear in older revisions of this repository and are **not** part of the
platform. They were removed to fit 3.7 GB of RAM per node:

- **MongoDB** — WiredTiger's memory appetite, and the 4.4 series is end-of-life.
- **Microsoft SQL Server** — does not fit alongside PostgreSQL and Redis.
- **RabbitMQ.**
- **Jenkins** — a JVM controller plus agents did not fit beside the data services;
  CI/CD is planned on Gitea Actions instead.
- **A dedicated DevOps node** — there is no fourth ThinkCentre.

## Operating-system reality

Ubuntu Server **22.04.5** on node1 and node3, **24.04** on node2, **26.04** on the
laptop. The repository's earlier claim of "24.04 LTS everywhere" is aspirational, not
current. Mixed versions are accepted
deliberately: reinstalling working data nodes to align a version number is not worth
the outage, and the Ansible roles are written to work on both.
