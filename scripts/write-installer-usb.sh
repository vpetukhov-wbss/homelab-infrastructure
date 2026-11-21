#!/usr/bin/env bash
# Write an installer ISO to a USB stick, with guards, and verify it by reading it back.
#
# Destructive: everything on the target stick is erased. Requires root.
# The target must be given as a /dev/disk/by-id/usb-* path (never /dev/sdX, which
# can change between plug-ins) and its exact size in bytes, so a wrong or
# swapped device is refused instead of overwritten.
set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: write-installer-usb.sh --iso FILE --device /dev/disk/by-id/usb-... --expect-bytes N --yes

  --iso FILE           image to write (e.g. installer/build/*.iso)
  --device PATH        target stick as /dev/disk/by-id/usb-* (whole disk, not a -partN)
  --expect-bytes N     exact size of the stick in bytes (blockdev --getsize64)
  --yes                required: confirms the stick will be erased
  -h, --help           show this help

Find the values with:
  ls -l /dev/disk/by-id/ | grep usb-
  sudo blockdev --getsize64 /dev/disk/by-id/usb-...
EOF
}

die() { echo "error: $*" >&2; exit 1; }

iso="" device="" expect="" yes=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --iso) iso="${2:-}"; shift 2 ;;
    --device) device="${2:-}"; shift 2 ;;
    --expect-bytes) expect="${2:-}"; shift 2 ;;
    --yes) yes=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
done

[[ -n "$iso" && -n "$device" && -n "$expect" ]] || { usage >&2; die "missing arguments"; }
[[ "$yes" -eq 1 ]] || die "refusing to erase anything without --yes"
[[ "$(id -u)" -eq 0 ]] || die "must run as root"
for c in dd blockdev lsblk findmnt sha256sum umount; do command -v "$c" >/dev/null || die "missing command: $c"; done
[[ -f "$iso" ]] || die "ISO not found: $iso"
[[ "$expect" =~ ^[0-9]+$ ]] || die "--expect-bytes must be a number"

# --- target guards ---------------------------------------------------------
[[ "$device" == /dev/disk/by-id/usb-* ]] || die "device must be a /dev/disk/by-id/usb-* path"
[[ "$device" != *-part[0-9]* ]] || die "give the whole disk, not a partition"
dev="$(readlink -f "$device")"
[[ -b "$dev" ]] || die "$device does not resolve to a block device"
name="${dev##*/}"
[[ "$(cat "/sys/block/$name/removable" 2>/dev/null)" == 1 ]] || die "$dev is not removable"
[[ "$(lsblk -dno TRAN "$dev")" == usb ]] || die "$dev is not on the USB bus"
size="$(blockdev --getsize64 "$dev")"
[[ "$size" == "$expect" ]] || die "$dev is $size bytes, expected $expect - wrong stick?"
root_disk="$(lsblk -no PKNAME "$(findmnt -no SOURCE /)" 2>/dev/null | head -1)"
[[ "$name" != "$root_disk" ]] || die "$dev holds the running root filesystem"
iso_bytes="$(stat -c %s "$iso")"
(( iso_bytes < size )) || die "image ($iso_bytes bytes) does not fit on $dev"

echo "target : $device -> $dev ($(lsblk -dno MODEL "$dev" | xargs), $size bytes)"
echo "image  : $iso ($iso_bytes bytes)"

# --- write -----------------------------------------------------------------
while read -r part mnt; do
  [[ -n "$mnt" ]] && { echo "unmounting /dev/$part ($mnt)"; umount "/dev/$part"; }
done < <(lsblk -nlo NAME,MOUNTPOINT "$dev" | tail -n +2)

dd if="$iso" of="$dev" bs=4M conv=fsync oflag=direct status=none
sync

# --- verify by reading the device back (not the page cache) ----------------
blockdev --flushbufs "$dev"
want="$(sha256sum < "$iso" | cut -d' ' -f1)"
got="$(head -c "$iso_bytes" "$dev" | sha256sum | cut -d' ' -f1)"
if [[ "$want" == "$got" ]]; then
  echo "VERIFIED: stick content matches image (sha256 $want)"
else
  die "read-back MISMATCH: wrote $want, read $got - do not boot from this stick"
fi
