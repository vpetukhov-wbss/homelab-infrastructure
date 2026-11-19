# Security Posture

> **Status:** verified by direct inspection on **2026-11-19**. Every control below was
> checked against the running systems, not against the Ansible roles that are supposed
> to produce them — the two had already diverged once (see
> [sshd override](#the-sshd-override-worth-understanding)).
>
> [`SECURITY.md`](../../SECURITY.md) states the policy. This document records what is
> actually enforced, and where it is not.

## Exposure map

What each machine listens on, and from where it can be reached.

| Host | Public (`0.0.0.0`) | Overlay-only (`10.44.0.x`) | Loopback-only |
|---|---|---|---|
| node1-gateway | 22​22 (SSH), 80, 443 (Caddy) | 2022 SFTP, 8200 Vault, 26379 Sentinel | — |
| node2-data-replica | 22 (SSH) | 5432 PostgreSQL, 6379 Redis, 26379 Sentinel | — |
| node3-data | 22 (SSH) | 5432 PostgreSQL, 6379 Redis, 26379 Sentinel | — |
| laptop | 2345 (SSH), 8080-8082 (unrelated `cakeshop` Caddy, HTTPS) | — | 5433 `cakeshop` PostgreSQL |

**No database port is bound to `0.0.0.0` on any machine.** PostgreSQL, Redis, Sentinel,
Vault and SFTP bind their overlay address explicitly, so they are unreachable from the
LAN even with a permissive firewall. This socket-level binding — not the firewall — is
the primary control, which matters because the LAN is not trustworthy (see below).

The laptop's `cakeshop` stack is unrelated to this platform. Its PostgreSQL is
correctly loopback-only; its Caddy exposes 8080-8082 to the LAN.

## The LAN is not a trust boundary

Packet capture on the wired segment shows ARP traffic from other subscribers' CPE
devices in the ISP's `10.209.0.0/16` range, leaking through the cable modem. Treat the
LAN as semi-public: no control in this platform may rest on "only our devices are on
this network". This is the reason databases bind to the overlay rather than being
firewalled on the LAN.

## Verified controls

### SSH is key-only on all four machines

Confirmed two ways — the effective configuration and a real connection attempt:

~~~bash
sshd -T | grep -E '^(passwordauthentication|permitrootlogin)'
# => passwordauthentication no / permitrootlogin no   on all four

ssh -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    -o BatchMode=yes -p <port> probe@<host> true
# => Permission denied (publickey)   on node1, node2, node3
~~~

### Firewall

`ufw` with default-deny inbound on all three nodes. node2 and node3 carry only the
intended rules — SSH, the WireGuard port, and the `wg1` interface. node1 additionally
allows 80/443 for Caddy and 51820/udp for the legacy `wg0`.

### Overlay

WireGuard `wg1` full mesh, each peer pinned to a single `/32` `AllowedIPs`, all
handshakes fresh. Private keys live only in `/etc/wireguard/` at mode `0600`.

### Secrets

No credential is committed. Ansible secrets are `ansible-vault`-encrypted in
git-ignored files; the vault password lives outside the repository at mode `0600`.
The WireGuard configuration backup is `ansible-vault`-encrypted at rest.

### SFTP mirror key

The mirror's ed25519 key is dedicated to lsyncd and constrained in node3's
`authorized_keys` to node1's overlay address with no pty and no forwarding:

~~~
from="10.44.0.1",no-pty,no-agent-forwarding,no-port-forwarding,no-X11-forwarding
~~~

## Known gaps

| Gap | Risk | Fix |
|---|---|---|
| **Vault is uninitialised and sealed** | No runtime secret store exists; secrets sit in ansible-vault files instead | `vault operator init` on node1; store unseal keys and root token offline, never in the repo |
| **node1 allows SSH from `Anywhere`** | Redundant with the LAN and overlay rules that already cover legitimate access; widens reach if node1 is ever port-forwarded | `ufw delete allow 2222/tcp`, keeping the `192.168.0.0/24` and `10.10.10.0/24` rules |
| **Caddy `:443` has no TLS site** | Port open, nothing serving it | Define a TLS site or close 443 until one exists |
| **Laptop not on the overlay** | Control plane reaches the fleet over the LAN, not the encrypted overlay | Bring up `wg1` as `10.44.0.4` |
| **Laptop SSH is Internet-forwarded** | Router forwards `2345` from the Internet to the laptop; key-only, but it is the one genuinely public attack surface | Restrict the forward by source address, or remove it and use WireGuard |
| **Single reused password** | The same password serves sudo on all three nodes | Rotate per-host once Vault is initialised |
| **Unpushed commits** | Infrastructure code exists only on the laptop | Push to the GitHub remote |

## The sshd override, worth understanding

node1 and node2 accepted SSH **passwords** for days while the Ansible role reported
success on every run. The mechanism is worth internalising because it generalises:

`sshd` honours the **first** value it obtains for a keyword, and
`Include /etc/ssh/sshd_config.d/*.conf` expands the glob **alphabetically**. Ubuntu
cloud images ship `50-cloud-init.conf` containing `PasswordAuthentication yes`, which
therefore beat the hardening file named `99-homelab-hardening.conf`. node3 was
unaffected only because it happens to have no cloud-init drop-in.

The role also validated its work with `sshd -t`, which checks **syntax** and cannot
detect an overridden directive. That is precisely why the gap survived.

Both halves are now fixed: the drop-in is named `00-homelab-hardening.conf` so it
always sorts first, and the role asserts the effective configuration from `sshd -T`
and fails the play if password or root login is enabled.

**The general lesson: verify the effective state, not the configuration you wrote.**
A role that reports success proves only that the role ran.

## Re-running the audit

~~~bash
cd ansible

# Effective SSH configuration on every machine
ansible all -b -m shell -a "sshd -T | grep -E '^(passwordauthentication|permitrootlogin)'"

# Port exposure: anything on 0.0.0.0 that is not SSH or Caddy is a finding
ansible homelab -b -m shell -a "ss -ltn"

# Firewall rules
ansible homelab -b -m shell -a "ufw status"

# Overlay health: every peer should have a recent handshake
ansible homelab -b -m shell -a "wg show wg1 latest-handshakes"
~~~

Re-running `playbooks/security.yml` re-applies and re-asserts SSH hardening and the
firewall; it fails loudly if either has drifted.
