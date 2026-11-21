# Security Posture

> **Status:** verified by direct inspection, last updated **2026-11-21**. Every control
> below was checked against the running systems, not against the Ansible roles that are
> supposed to produce them. The two had diverged more than once — see
> [the overlay binding that protected nothing](#the-overlay-binding-that-protected-nothing)
> and [the sshd override](#the-sshd-override-worth-understanding).
>
> [`SECURITY.md`](../../SECURITY.md) states the policy. This document records what is
> actually enforced, and where it is not.

## Exposure map

What each machine listens on, and from where it can be reached.

| Host | Reachable from the LAN | Overlay only (`10.44.0.x`, via `wg1`) | Loopback only |
|---|---|---|---|
| node1-gateway | 2222 SSH (LAN subnet + legacy `wg0` only), 80, 443 Caddy | 2022 SFTP, 8200 Vault, 26379 Sentinel, 5672 + 15672 RabbitMQ | — |
| node2-data-replica | 22 SSH (LAN subnet only) | 5432 PostgreSQL, 6379 Redis, 26379 Sentinel | — |
| node3-data | 22 SSH (LAN subnet only) | 5432 PostgreSQL, 6379 Redis, 26379 Sentinel, 8080 nginx, 3000 + 2223 Gitea | — |
| laptop | 2345 SSH | — | — |

The laptop is about to be reinstalled as `node4`; its row will change. Until then it is
a desktop with `ufw` inactive and is not part of these guarantees.

## The overlay guard — the control that actually keeps services private

Every database, broker, Vault and application port is published by Docker on a
`10.44.0.x` address. **That binding alone does not keep it off the LAN.** Docker's DNAT
rules match the destination address on *any* ingress interface, Linux accepts packets
for its own overlay address arriving on `eno1`, and DNAT'd traffic never passes ufw's
INPUT chain. A LAN host only has to add a route:

~~~bash
ip route add 10.44.0.3/32 via 192.168.0.103   # then connect to 10.44.0.3:<port>
~~~

On 2026-11-21 that reached **14 of 14** overlay-published ports from the laptop —
Vault, RabbitMQ, SFTP, all Sentinels, both PostgreSQL servers, both Redis servers,
Gitea and the app host.

The fix is `homelab-overlay-guard` (firewall role): a `raw`-table chain, evaluated before
any NAT, that drops every packet for `10.44.0.0/24` unless it arrived on `wg1`, loopback
or a Docker bridge. Re-tested the same way afterwards: **14 of 14 blocked**, and
PostgreSQL replication, Redis replication, Sentinel and Caddy's proxying all unaffected.

The same unit installs `unreachable 10.44.0.0/24 metric 4000`. The overlay range also
exists inside the ISP's network (`10.44.0.1` answers from about six hops away), so
without that route a node whose `wg1` is down would send overlay traffic — including
Redis `AUTH` — towards the ISP. `wg1`'s own route (metric 0) wins whenever it is up.

## The LAN is not a trust boundary

Packet capture on the wired segment shows ARP traffic from other subscribers' CPE
devices in the ISP's `10.209.0.0/16` range, leaking through the cable modem. Treat the
LAN as semi-public: no control in this platform may rest on "only our devices are on
this network".

## Verified controls

### SSH

Key-only on every machine, verified against `sshd -T` and a real password attempt.
Since 2026-11-21 also: `X11Forwarding no`, `AllowAgentForwarding no`,
`AllowTcpForwarding local`, `MaxAuthTries 3`. ufw accepts SSH only from the LAN subnet
and `wg1` — never "Anywhere" — so an accidental router forward cannot expose it.

### Ansible verifies host keys

`host_key_checking = True`. Keys are pinned in `~/.ssh/known_hosts` under the inventory
name (`HostKeyAlias`), so node1 drifting between DHCP leases does not break
verification. The pinned keys were checked against each host's own key file over an
authenticated session, and a deliberately wrong key is refused
(`REMOTE HOST IDENTIFICATION HAS CHANGED`).

### Firewall

`ufw` default-deny inbound and default-deny routed on all three nodes (node1 had drifted
to "allow routed"). Inbound: SSH from the LAN subnet, the WireGuard port, everything on
`wg1`; node1 additionally 80/443 and the legacy `wg0` port.

### Redis and Sentinel

Redis requires a password. Since 2026-11-21 **Sentinel does too** (`requirepass` plus
`sentinel-pass` between Sentinels); before that anyone who could reach port 26379 could
force a failover or point clients — and the backup job, carrying the Redis password — at
a host of their choosing. Healthchecks and tooling authenticate through `REDISCLI_AUTH`,
never `-a <password>` on a command line.

### CI runner

Jobs run in containers **without** the Docker socket (`container.docker_host: "-"`),
capped at 1 GB / 1 CPU / 512 PIDs, one at a time. The `:host` label is gone. Only the
runner daemon itself holds the socket, which it needs to start job containers.

### Vault

Initialised on 2026-11-21 with **one unseal key** (single owner). The key and the initial
root token are on paper with the owner — never on a node, in the repository or in a chat.
Only after init was it moved from `127.0.0.1` to the overlay (`10.44.0.1:8200`): node2
and node3 reach it over `wg1`, and the LAN-route test is blocked for 8200 as for every
other overlay port. Vault re-seals on every restart and is unsealed by hand.

The first init's output was pasted into a chat transcript. Vault was still empty, so it
was reset (data directory replaced) and initialised again; the leaked key opens nothing.
See `docs/runbooks/vault-init.md`.

### Secrets

No credential is committed. Ansible secrets are `ansible-vault`-encrypted in git-ignored
files; the vault password lives outside the repository at mode `0600`. Tasks that render
secrets use `no_log`/`diff: false`, so `--diff` and `-v` do not print them. Files that
held passwords at `0644` (the old single-file Redis and Sentinel configs) were removed.

### Fail2Ban

sshd jail active on every machine, banning through `ufw`. `ignoreip` is loopback, the
overlay and the controller's own address — **not** the whole LAN, which is hostile and
must be bannable. The role fails the play if that policy does not hold.

### Backups

AES-256 encrypted (openssl, pbkdf2, 200k iterations), verified by decrypting and listing
before the archive is given its name, and **copied to node3's disk** after every run.
Credentials reach the tools through environment variables, never argv.

### Sync key (SFTP mirror and backup)

One dedicated ed25519 key, generated on node1. On node3 it is accepted only for the
unprivileged `homelab-sync` account (no `docker`, no `sudo`), only from `10.44.0.1`, and
only as `rrsync /srv/homelab-sync/data`:

~~~
restrict,from="10.44.0.1",command="/usr/bin/rrsync /srv/homelab-sync/data" ssh-ed25519 …
~~~

The account's `authorized_keys` is root-owned and outside the rrsync root. The role
proves a shell command is refused. Before 2026-11-21 the key logged in as `vpetukhov` —
root through the `docker` group — so root on node1 meant root on node3.

## Known gaps

| Gap | Risk | Fix |
|---|---|---|
| **Vault holds nothing yet; the root token is live** | Secrets still sit in ansible-vault files; the initial root token can do anything | Admin policy + identity, then revoke the root token; raft snapshot in the backup (roadmap M7) |
| **No TLS at the edge** | Gitea logins cross the LAN in clear text | A DNS name + certificate before any router forward |
| **Backup key next to the archives** | Whoever takes node1's disk can decrypt its archives | Encrypt to an `age` public key; keep the identity offline |
| **No off-site backup** | Both copies are in the same building | Weekly copy off site |
| **`docker` group = root** | The admin SSH key alone is root on every node | Move compose tasks to `become`, drop the group |
| **Laptop SSH is Internet-forwarded** | The one genuinely public attack surface | Remove the forward; use WireGuard for remote access |
| **Legacy `wg0` on node1 and the laptop** | Two never-used tunnels, same address, open port | Decide on one remote-access design; remove the other |
| **Single reused sudo password** | One password for every node | Per-host passwords, kept in Vault (initialised, still empty) |
| **PostgreSQL superuser over the overlay** | Every user incl. superuser may connect from `10.44.0.0/24` | A `pg_read_all_data` role for backups; superuser local-only |
| **End-of-life versions** | Gitea 1.22, RabbitMQ 3.13 no longer receive fixes | Planned upgrades, after a backup |

## The overlay binding that protected nothing

Until 2026-11-21 this document said the overlay bind address made services
"unreachable from the LAN even with a permissive firewall", and that `node3:8080` had
been "confirmed unreachable from the LAN". Both checks had tested the *LAN* address of
the node, which was never exposed. Nobody had tested the overlay address through a LAN
route — the one path an attacker would use. The lesson is the same as the sshd one
below: **test the attacker's path, not the one you configured.**

## The sshd override, worth understanding

node1 and node2 accepted SSH **passwords** for days while the Ansible role reported
success on every run. `sshd` honours the **first** value it obtains for a keyword, and
`Include /etc/ssh/sshd_config.d/*.conf` expands **alphabetically**: Ubuntu cloud images
ship `50-cloud-init.conf` with `PasswordAuthentication yes`, which beat a hardening file
named `99-…`. The role validated with `sshd -t` (syntax only). It now writes
`00-homelab-hardening.conf` and asserts the effective configuration from `sshd -T`.

## Re-running the audit

~~~bash
cd ansible

# Effective SSH configuration on every machine
ansible all -b -m shell -a "sshd -T | grep -E '^(passwordauthentication|permitrootlogin|x11forwarding|maxauthtries) '"

# The attacker's path: from a LAN machine that is NOT on wg1, route one overlay address
# via the node's LAN IP and try every published port. Expect every attempt to fail.
sudo ip route add 10.44.0.3/32 via 192.168.0.103
for p in 3000 2223 5432 6379 8080 26379; do timeout 3 bash -c "</dev/tcp/10.44.0.3/$p" && echo "OPEN $p"; done
sudo ip route del 10.44.0.3/32 via 192.168.0.103

# The guard itself, and the fallback route
ansible edge:data_primary:data_replica -b -m shell -a "iptables -t raw -S HOMELAB-OVERLAY-GUARD; ip route show type unreachable"

# Overlay health: every peer should have a recent handshake
ansible edge:data_primary:data_replica -b -m shell -a "wg show wg1 latest-handshakes"
~~~

Re-running `playbooks/security.yml` re-applies and re-asserts SSH hardening, the firewall
and the overlay guard; it fails loudly if any of them has drifted.
