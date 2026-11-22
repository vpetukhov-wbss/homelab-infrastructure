# Bare-Metal Installer

Builds a node-specific, unattended Ubuntu Server installer on a USB stick. It produces
a bare host that Ansible can reach — nothing more. Roles (Docker, WireGuard, firewall,
services) are applied afterwards from the inventory.

**Status:** built for `node4` (ThinkPad L470) on 2026-11-21 with Ubuntu Server
**26.04.1 LTS**; not installed yet. Older nodes run 22.04/24.04; versions are not aligned on
purpose (see `docs/PROJECT-CONTEXT.md` §2).

| File | Purpose |
|---|---|
| `installer/autoinstall/autoinstall.template.yaml` | Generic autoinstall config with `@DISK_SERIAL@`; public, no secrets |
| `scripts/build-installer-iso.py` | Renders the template for one machine, schema-validates it, embeds it in a verified ISO |
| `scripts/write-installer-usb.sh` | Writes the image to a USB stick with device guards and a read-back check |
| `installer/homelab-restore.sh` | Installed on the node as `homelab-restore`; rebuilds the controller from the bundle |
| `scripts/make-recovery-bundle.sh` | Produces the encrypted controller bundle the stick can carry |
| `installer/build/` | Rendered config and images — **git-ignored** (real serials and keys) |

## What the installed host gets

- LVM on the whole disk (`sizing-policy: all` — avoids the ~100 GB under-allocation trap)
- OpenSSH, **key-only** (`allow-pw: false`), with the controller and admin public keys;
  with `--ssh-port N`, sshd listens on N instead of 22 (plain `ssh.service`, socket
  activation off) — node4 uses 2345 so the router's forward, the owner's remote path,
  keeps working after the reinstall
- Wired NIC on DHCP (`en*`, optional). With `--wifi-*`: Wi-Fi for the installed system,
  joined on its first boot, on DHCP or on the static address given with
  `--wifi-address`/`--wifi-gateway`/`--wifi-dns`. Without a cable the install itself
  runs offline from the stick
- Security updates applied during install when a cable gives the installer a network (it
  never uses the Wi-Fi); UTC; no crash-kernel reservation
- Suspend/hibernate masked and lid switch ignored, so laptop hardware stays reachable
  until Ansible takes over
- With `--recovery-bundle`: the encrypted controller bundle and `homelab-restore`
  (see below)

## Wi-Fi

networkd cannot match Wi-Fi by pattern, so the interface is named (`--wifi-iface`,
default `wlp5s0`). The stick carries the **64-hex PSK derived from passphrase + SSID**,
not the passphrase: it can join that one network but does not reveal the password. Derive
it on a machine that already knows the network, without printing it:

~~~bash
nmcli -s -g 802-11-wireless-security.psk connection show "<SSID>" \
 | python3 -c 'import sys,hashlib; p=sys.stdin.read().rstrip("\n"); print(hashlib.pbkdf2_hmac("sha1",p.encode(),sys.argv[1].encode(),4096,32).hex())' "<SSID>" \
 > psk.hex && chmod 600 psk.hex
~~~

**The installer itself never touches Wi-Fi.** An autoinstall `network:` section is
applied in the live session with `netplan apply`, which synchronously starts
`netplan-regdom.service` (`iw reg set`; `iw` is not on the ISO) and
`netplan-wpa-*.service` (`wpa_supplicant` only reaches the live session after the card
is detected). Either failing aborts the install before the disk is touched. That is
what a VM test of the first stick hit on 2026-11-21. So the build writes
`/etc/netplan/60-homelab-wifi.yaml` (mode 600) into the *target* in `late-commands`.
It sets the country through `cfg80211 ieee80211_regdom=` (rules from `wireless-regdb`,
already in the image) instead of `iw`.

`wpasupplicant` is not in the Ubuntu Server image; the build adds it to `packages`
(installed offline from the ISO's pool), otherwise the installed system could not join
Wi-Fi at first boot. Only WPA2-PSK; a WPA3-only (SAE) network needs the passphrase.

**Keep the address the machine had.** The laptop's address was static, set in
NetworkManager rather than reserved on the router, and the router's 2345 forward points
at it. A DHCP install would most likely come up on another address: the install
succeeds and the owner's only remote path is gone. So node4's stick pins the same
address, gateway and DNS servers. Read them on the running machine before a rebuild:

~~~bash
nmcli -f ipv4.method,ipv4.addresses,ipv4.gateway,ipv4.dns connection show "<SSID>"
~~~

## Recovery bundle → "install and everything is there"

`--recovery-bundle` embeds an **encrypted** bundle from `scripts/make-recovery-bundle.sh`
(the script refuses an unencrypted file). The install copies it to
`/var/lib/homelab-restore/` and installs `/usr/local/sbin/homelab-restore`. At the first
login a message says to run:

~~~bash
sudo homelab-restore
~~~

It asks for the homelab password once and restores SSH keys, the vault password, the
repository (full history, uncommitted files, git-ignored inventory and vault files),
WireGuard backups and Claude Code's project memory, rebuilds the Ansible virtualenv and
offers to install Claude Code. If GitHub is reachable, it then fast-forwards the
repository to the latest pushed commit, so a stick a little older than GitHub still
restores the current state. The password never goes on the stick; without it the
bundle is useless to whoever finds the stick. **Wipe or re-flash the stick after the
install anyway** — it still carries the Wi-Fi PSK.

Not installed here: Docker, WireGuard, firewall, any service. That is Ansible's job.

## Safety design — booting the stick in the wrong machine wipes nothing

1. The stock boot menu has no `autoinstall` kernel argument, and the config keeps
   `identity` interactive, so the installer shows the *Profile configuration* screen and
   then a **"Confirm destructive action"** dialog (default **No**) before it writes
   anything. It never asks "Continue with autoinstall?": that prompt belongs to a fully
   non-interactive autoinstall. Never add the argument.
2. `early-commands` aborts unless a disk with the expected serial is present.
3. `storage.layout.match` accepts only that serial. No match → `AutoinstallError`
   before partitioning.

Identity (hostname, username, password) is typed at the console, so no password hash is
baked into the image.

These behaviours were checked against the subiquity 26.04.1 source shipped on the ISO
(`server/controllers/install.py` confirmation gate, `models/filesystem.py` serial match,
`server/server.py` reading `/cdrom/autoinstall.yaml`).

## Build

```bash
mkdir -p ~/homelab-iso/download && cd ~/homelab-iso/download
V=26.04.1
curl -fLO https://releases.ubuntu.com/$V/SHA256SUMS -fLO https://releases.ubuntu.com/$V/SHA256SUMS.gpg
curl -fLO https://releases.ubuntu.com/$V/ubuntu-$V-live-server-amd64.iso

# Signature: must say "Good signature" from key 8439 38DF 228D 22F7 B374 2BC0 D94A A3F0 EFE2 1092
G=$(mktemp -d); gpg --homedir "$G" --keyserver hkps://keyserver.ubuntu.com --recv-keys 843938DF228D22F7B3742BC0D94AA3F0EFE21092
gpg --homedir "$G" --verify SHA256SUMS.gpg SHA256SUMS

# Schema of the SAME installer release, for validation
curl -fL -o ~/homelab-iso/autoinstall-schema.json \
  https://raw.githubusercontent.com/canonical/subiquity/$V/autoinstall-schema.json

# Target disk serial: on the target, `udevadm info -q property -n /dev/sda | grep ID_SERIAL_SHORT`
cd /path/to/homelab-infrastructure
scripts/build-installer-iso.py \
  --source-iso ~/homelab-iso/download/ubuntu-$V-live-server-amd64.iso \
  --sha256sums ~/homelab-iso/download/SHA256SUMS \
  --schema ~/homelab-iso/autoinstall-schema.json \
  --name node4 --disk-serial <DISK_SERIAL> \
  --ssh-key ~/.ssh/homelab-controller.pub --ssh-key ~/.ssh/<admin-pc>.pub \
  --ssh-port 2345 \
  --wifi-ssid "<SSID>" --wifi-psk-hex-file psk.hex \
  --wifi-address <LAN-IP>/24 --wifi-gateway <router-IP> --wifi-dns <dns>[,<dns>] \
  --recovery-bundle ~/homelab-recovery/homelab-recovery-YYYYMMDD-HHMM.tar.gpg
```

Expected output ends with `autoinstall config valid`, `boot records replayed: 2 El Torito
image(s)` and the image's sha256.

## Write the stick (destructive)

```bash
ls -l /dev/disk/by-id/ | grep usb-                    # pick the stick
sudo blockdev --getsize64 /dev/disk/by-id/usb-...     # its exact size
sudo scripts/write-installer-usb.sh --iso installer/build/<image>.iso \
  --device /dev/disk/by-id/usb-... --expect-bytes <size> --yes
```

It refuses anything that is not a removable USB disk of exactly that size, or that holds
the running root filesystem, and ends with `VERIFIED` after reading the stick back.

## Install

1. On AC power. Network: the Wi-Fi configured on the stick. Have a cable ready, but do
   not plug it in during the install (the offline install is what was tested): joining the
   Wi-Fi with the static address is untested on the real hardware, and `homelab-restore`
   needs the Internet (apt, pip, GitHub), so plug the cable in after the first boot if
   the Wi-Fi did not come up. In the laptop BIOS enable **Power On with AC Attach**, so
   the headless server starts again after a power cut; the install itself does not need it.
2. Boot the stick (ThinkPad: F12, the UEFI entry for the stick). Its boot menu starts
   *Try or Install Ubuntu Server* by itself after 30 seconds.
3. **Profile configuration**: *Your servers name* is the node name (`node4`); *Pick a
   username* **must equal** `ansible_user` in the inventory (`vpetukhov`); the password
   must equal the become password in the vault (or update the vault); *Your name* is free
   text. Choose *Done*.
4. **"Confirm destructive action"**: *Continue* starts the install and wipes the disk with
   the configured serial; *No*, the default, aborts before anything is written. This is
   the last chance to abort. There is no `yes` to type.
5. The machine reboots into the new system. Remove the stick.

## Validate (from the controller)

```bash
ssh -i ~/.ssh/homelab-controller -p <ssh-port> vpetukhov@<new-ip> '
  sudo sshd -T | grep -E "^(passwordauthentication|pubkeyauthentication) ";  # no / yes
  sudo vgs -o vg_name,vg_free;                                              # VFree ~0
  systemctl is-enabled sleep.target;                                        # masked
  lsblk -o NAME,SIZE,MOUNTPOINTS'
```

Then add the host to `inventory/hosts.yml` and run the base, overlay, security and
fail2ban playbooks (`docs/PROJECT-CONTEXT.md` §10).

## After the restore (node4)

Before `sudo homelab-restore` check the network: `ip -4 -br addr show wlp5s0; getent hosts
github.com pypi.org archive.ubuntu.com` must show the pinned address and three names. The
restore needs the Internet from step 7 on (`python3-venv` is not on the ISO). If the Wi-Fi
did not come up, plug in a cable after the first boot; do not plug it in during the install.

1. `sudo homelab-restore`, then `cd ~/Projects/homelab-infrastructure/ansible &&
   ~/homelab/.venv/bin/ansible homelab -m ping`: four pongs. The node4 line is local and
   always passes, so the three servers are the real test.
2. `ip -4 -br addr show wlp5s0` must show the pinned address: the inventory's
   `controller_lan_ip` and the target of the router's 2345 forward. Reserve it for the
   Wi-Fi MAC on the router as well, so DHCP never hands it to another device.
3. Check what the restore does not guarantee:
   - `ls -l ~/Projects/homelab-infrastructure/inventory/hosts.yml ~/.ssh/homelab-controller
     ~/.homelab_vault_pass`: three files, the two secrets mode 600. If the repository step
     was interrupted, move `~/Projects/homelab-infrastructure` aside and run the restore
     again;
   - `~/.local/bin/claude --version`: the script prints "installed" even when the download
     failed;
   - `install -m 600 ~/.homelab_vault_pass ~/.homelab_recovery_pass`: the next
     `make-recovery-bundle.sh` needs it and the restore does not write it;
   - `git config --global user.name` and `user.email`: `~/.gitconfig` is not in the bundle,
     and the repository is public.
4. If step 7 aborted for lack of network, steps 1 to 6 are done. Once the network works:
   `sudo apt-get install -y python3-venv && python3 -m venv ~/homelab/.venv &&
   ~/homelab/.venv/bin/pip install --upgrade pip ansible && sudo touch
   /var/lib/homelab-restore/restored`.
5. The install generated new SSH host keys. Compare the fingerprint from `ssh-keygen -lf
   /etc/ssh/ssh_host_ed25519_key.pub` on the console before you accept it on a client,
   then run `ssh-keygen -R '[192.168.0.35]:2345'` there. Only the controller and Legion
   keys are on the stick: add the public key of any other admin machine to
   `~/.ssh/authorized_keys`.
6. Add node4 to the overlay plays (`wg_overlay_ip: 10.44.0.4`), then run `overlay.yml`,
   `security.yml`, `fail2ban.yml` and `base.yml` (roadmap M3). Do not talk to
   `10.44.0.x` before `wg1` is up: that range also exists inside the ISP's network.
   Before `security.yml`, make the `firewall` role take the SSH port from `sshd -T`
   (node4 connects locally and has no `ansible_port`) and give node4 an explicit
   exception that keeps 2345 reachable from outside until WireGuard remote access exists:
   the role allows SSH from the LAN only and deletes the any-source rule, so without it
   the owner is locked out. Apply it with a rollback timer.
7. Wipe or re-flash the installer stick only after node4 pings all three servers and the
   bundle has two verified copies that are not on the stick.

## Recovery and rollback

- Fails **before** the storage step (confirmation refused, serial mismatch, schema error):
  the old disk is untouched; reboot without the stick.
- Fails **after** partitioning: the disk is already wiped; rerun the install. Anything that
  lived only on that machine must come from the recovery bundle — create and test it first.

## Limitations

- Wired NIC: DHCP only — reserve its MAC on the router so WireGuard endpoints stay
  stable. Wi-Fi: DHCP, or a static IPv4 address with `--wifi-address`.
- No disk encryption (a headless server cannot type a passphrase at boot).
- Identity is typed by hand; a typo in the username breaks Ansible until fixed.
