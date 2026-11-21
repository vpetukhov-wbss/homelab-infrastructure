#!/usr/bin/env python3
"""Build a node-specific Ubuntu Server autoinstall ISO.

Renders installer/autoinstall/autoinstall.template.yaml for one machine,
validates it against the installer's autoinstall JSON schema, and embeds it as
/autoinstall.yaml in a copy of an official, already-verified Ubuntu Server ISO.
The boot records are replayed unchanged, so the image boots exactly like the
original (BIOS and UEFI, Secure Boot shim intact).

Optional:
  --wifi-*            join a WPA2-PSK network during and after the install. Give the
                      64-hex PSK (derived from passphrase + SSID), never the passphrase:
                      it grants network access but does not reveal the password.
  --recovery-bundle   embed an ENCRYPTED controller bundle plus the homelab-restore
                      tool; after the first login, `sudo homelab-restore` asks for the
                      password and rebuilds keys, vault, inventory and repository.
  --ssh-port          listen on this port instead of 22 (e.g. the port the router
                      forwards), so remote access survives the reinstall.

The rendered config holds real inventory (disk serial, keys, Wi-Fi PSK), so it
goes to installer/build/ (git-ignored, mode 600). Writing the image to a USB
stick is deliberately NOT done here — see scripts/write-installer-usb.sh.

Example:
  scripts/build-installer-iso.py \\
    --source-iso ~/homelab-iso/download/ubuntu-26.04.1-live-server-amd64.iso \\
    --sha256sums ~/homelab-iso/download/SHA256SUMS \\
    --schema ~/homelab-iso/schema/autoinstall-schema.json \\
    --name node4 --disk-serial S3S7NF0JB05893 \\
    --ssh-key ~/.ssh/homelab-controller.pub \\
    --wifi-ssid MyNet --wifi-psk-hex-file /path/psk.hex \\
    --recovery-bundle ~/homelab-recovery/homelab-recovery-YYYYMMDD-HHMM.tar.gpg
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parent.parent
TEMPLATE = REPO / "installer" / "autoinstall" / "autoinstall.template.yaml"
RESTORE_TOOL = REPO / "installer" / "homelab-restore.sh"
RESTORE_HINT = REPO / "installer" / "homelab-restore-hint.sh"
BUILD_DIR = REPO / "installer" / "build"
SERIAL_RE = re.compile(r"^[A-Za-z0-9._-]{4,64}$")
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,30}$")
IFACE_RE = re.compile(r"^[a-z0-9]{2,15}$")
HEXPSK_RE = re.compile(r"^[0-9a-f]{64}$")
PUBKEY_RE = re.compile(r"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com) [A-Za-z0-9+/=]+( .*)?$")


def die(msg: str) -> None:
    print(f"error: {msg}", file=sys.stderr)
    sys.exit(1)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(8 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def verify_source(iso: Path, sums: Path | None) -> None:
    if sums is None:
        print("warning: --sha256sums not given; source ISO integrity NOT checked", file=sys.stderr)
        return
    wanted = None
    for line in sums.read_text().splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[1].lstrip("*") == iso.name:
            wanted = parts[0].lower()
    if wanted is None:
        die(f"{iso.name} is not listed in {sums}")
    print(f"checking sha256 of {iso.name} ...")
    if sha256(iso) != wanted:
        die("source ISO checksum MISMATCH - re-download it")
    print("source ISO checksum OK (verify SHA256SUMS.gpg separately, see installer/README.md)")


def read_pubkey(path: Path) -> str:
    text = path.read_text()
    if "PRIVATE KEY" in text:
        die(f"{path} is a PRIVATE key - pass the .pub file")
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    if len(lines) != 1 or not PUBKEY_RE.match(lines[0]):
        die(f"{path} must contain exactly one OpenSSH public key line")
    subprocess.run(["ssh-keygen", "-lf", str(path)], check=True, stdout=subprocess.DEVNULL)
    return lines[0]


def render(serial: str, keys: list[str], schema: Path | None, wifi: dict | None, recovery: bool,
           ssh_port: int = 22) -> str:
    text = TEMPLATE.read_text().replace("@DISK_SERIAL@", serial)
    leftover = re.findall(r"@[A-Z_]+@", text)
    if leftover:
        die(f"unrendered placeholders: {sorted(set(leftover))}")
    data = yaml.safe_load(text)
    ai = data["autoinstall"]
    ai["ssh"]["authorized-keys"] = keys

    if wifi:
        ai["network"]["wifis"] = {
            wifi["iface"]: {
                "dhcp4": True,
                "dhcp6": False,
                "regulatory-domain": wifi["country"],
                "access-points": {wifi["ssid"]: {"password": wifi["psk"]}},
            }
        }
        # Not in the server image; the ISO's offline pool has it. Without it the
        # installed system cannot join Wi-Fi at first boot.
        ai.setdefault("packages", [])
        if "wpasupplicant" not in ai["packages"]:
            ai["packages"].append("wpasupplicant")

    if recovery:
        # /var/lib/homelab-restore is 0711 so the login hint can see the marker files;
        # the bundle itself stays root-only.
        ai["late-commands"] += [
            "install -d -m 0711 /target/var/lib/homelab-restore",
            "install -m 0600 /cdrom/homelab/recovery.tar.gpg /target/var/lib/homelab-restore/recovery.tar.gpg",
            "install -m 0755 /cdrom/homelab/homelab-restore /target/usr/local/sbin/homelab-restore",
            "install -m 0644 /cdrom/homelab/homelab-restore-hint.sh /target/etc/profile.d/zz-homelab-restore.sh",
        ]

    if ssh_port != 22:
        # Classic ssh.service instead of socket activation, so sshd itself reads Port and
        # nothing depends on the socket generator - the same setup the laptop ran before.
        ai["late-commands"] += [
            f"printf 'Port {ssh_port}\\n' > /target/etc/ssh/sshd_config.d/00-homelab-port.conf",
            "curtin in-target --target=/target -- systemctl disable ssh.socket",
            "curtin in-target --target=/target -- systemctl enable ssh.service",
        ]

    if schema is None:
        print("warning: --schema not given; autoinstall NOT schema-validated", file=sys.stderr)
    else:
        import jsonschema  # only needed when validating

        jsonschema.validate(ai, json.loads(schema.read_text()))
        print(f"autoinstall config valid against {schema.name}")

    header = "# GENERATED by scripts/build-installer-iso.py - do not edit, do not commit.\n"
    return header + yaml.safe_dump(data, sort_keys=False, default_flow_style=False, width=1000)


def build_iso(src: Path, config: Path, out: Path, extra: list[tuple[Path, str]]) -> None:
    if out.exists():
        out.unlink()
    cmd = ["xorriso", "-indev", str(src), "-outdev", str(out), "-map", str(config), "/autoinstall.yaml"]
    for local, iso_path in extra:
        cmd += ["-map", str(local), iso_path]
    cmd += ["-boot_image", "any", "replay"]
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # Read every embedded file back out of the new image and compare byte for byte.
    for local, iso_path in [(config, "/autoinstall.yaml")] + extra:
        check = out.with_suffix(".check")
        subprocess.run(
            ["xorriso", "-osirrox", "on", "-indev", str(out), "-extract", iso_path, str(check)],
            check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        same = check.read_bytes() == local.read_bytes()
        check.unlink()
        if not same:
            die(f"{iso_path} inside the built ISO differs from {local}")
    print(f"embedded and verified: {', '.join(p for _, p in [(config, '/autoinstall.yaml')] + extra)}")

    def boot_report(iso: Path) -> list[str]:
        r = subprocess.run(["xorriso", "-indev", str(iso), "-report_el_torito", "plain"],
                           check=True, capture_output=True, text=True)
        return [ln for ln in r.stdout.splitlines() if ln.startswith("El Torito boot img")]

    if len(boot_report(out)) != len(boot_report(src)) or not boot_report(out):
        die("boot records of the built ISO do not match the source ISO")
    print(f"boot records replayed: {len(boot_report(out))} El Torito image(s), same as source")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source-iso", type=Path, required=True)
    ap.add_argument("--sha256sums", type=Path, help="gpg-verified SHA256SUMS listing the source ISO")
    ap.add_argument("--schema", type=Path, help="autoinstall-schema.json of the same subiquity release")
    ap.add_argument("--name", required=True, help="node name, used only for output file names")
    ap.add_argument("--disk-serial", required=True, help="serial (ID_SERIAL_SHORT) of the ONLY disk allowed")
    ap.add_argument("--ssh-key", type=Path, action="append", required=True, help="public key file (repeatable)")
    ap.add_argument("--wifi-ssid", help="WPA2-PSK network to join")
    ap.add_argument("--wifi-psk-hex-file", type=Path, help="file holding the 64-hex PSK for --wifi-ssid")
    ap.add_argument("--wifi-iface", default="wlp5s0", help="Wi-Fi interface name (networkd cannot match Wi-Fi by glob)")
    ap.add_argument("--wifi-country", default="BG", help="regulatory domain")
    ap.add_argument("--recovery-bundle", type=Path, help="ENCRYPTED controller bundle (.tar.gpg) to embed")
    ap.add_argument("--ssh-port", type=int, default=22, help="sshd port on the installed system (default 22)")
    args = ap.parse_args()

    if not NAME_RE.match(args.name):
        die("--name must be lowercase letters, digits and dashes")
    if not 1 <= args.ssh_port <= 65535:
        die("--ssh-port must be 1-65535")
    if not SERIAL_RE.match(args.disk_serial):
        die("--disk-serial has unexpected characters")
    src = args.source_iso.expanduser().resolve()
    if not src.is_file():
        die(f"{src} not found")

    wifi = None
    if args.wifi_ssid or args.wifi_psk_hex_file:
        if not (args.wifi_ssid and args.wifi_psk_hex_file):
            die("--wifi-ssid and --wifi-psk-hex-file go together")
        psk = args.wifi_psk_hex_file.expanduser().read_text().strip()
        if not HEXPSK_RE.match(psk):
            die("--wifi-psk-hex-file must hold exactly 64 lowercase hex characters (a derived PSK, not a passphrase)")
        if not IFACE_RE.match(args.wifi_iface):
            die("--wifi-iface looks wrong")
        wifi = {"ssid": args.wifi_ssid, "psk": psk, "iface": args.wifi_iface, "country": args.wifi_country}

    extra: list[tuple[Path, str]] = []
    if args.recovery_bundle:
        bundle = args.recovery_bundle.expanduser().resolve()
        with bundle.open("rb") as f:
            head = f.read(512)
        # A tar carries "ustar" at offset 257; an OpenPGP message starts with a packet
        # tag byte that has the high bit set. Refuse anything that is not encrypted.
        if head[257:262] == b"ustar" or not head or not head[0] & 0x80:
            die("--recovery-bundle is not an OpenPGP (gpg) message - it must be the encrypted .tar.gpg")
        extra = [
            (bundle, "/homelab/recovery.tar.gpg"),
            (RESTORE_TOOL, "/homelab/homelab-restore"),
            (RESTORE_HINT, "/homelab/homelab-restore-hint.sh"),
        ]

    verify_source(src, args.sha256sums.expanduser() if args.sha256sums else None)
    keys = [read_pubkey(p.expanduser()) for p in args.ssh_key]

    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    config = BUILD_DIR / f"{args.name}.autoinstall.yaml"
    fd = os.open(config, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(render(args.disk_serial, keys, args.schema.expanduser() if args.schema else None,
                       wifi, bool(args.recovery_bundle), args.ssh_port))
    os.chmod(config, 0o600)
    out = BUILD_DIR / f"{src.stem}-{args.name}.iso"
    print(f"building {out.name} ...")
    build_iso(src, config, out, extra)
    print(f"config : {config}")
    print(f"image  : {out}")
    print(f"sha256 : {sha256(out)}")


if __name__ == "__main__":
    main()
