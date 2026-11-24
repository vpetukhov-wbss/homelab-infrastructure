#!/bin/sh
# Build this device's WireGuard configuration. Run it ON the device (Linux or macOS),
# next to peers.conf. The private key is generated here and never leaves the machine;
# only the public key it prints at the end goes to the controller.
#   sh make-wg-client.sh <device-name> <overlay-ip> <home-address>
#   sh make-wg-client.sh laptop 10.44.0.11 home.example.net
set -eu
[ $# -eq 3 ] || { sed -n '2,6s/^# //p' "$0" >&2; exit 2; }
name=$1 ip=$2 home=$3
case $name in *[!a-z0-9-]*|'') echo "device name: a-z, 0-9 and -" >&2; exit 2 ;; esac
# the file name is the interface name, which Linux limits to 15 characters
[ ${#name} -le 12 ] || { echo "device name: at most 12 characters" >&2; exit 2; }
case $ip in 10.44.0.*) ;; *) echo "overlay ip must be 10.44.0.x" >&2; exit 2 ;; esac
case $home in *[!A-Za-z0-9.-]*|'') echo "home address: a hostname or an IPv4 address" >&2; exit 2 ;; esac
command -v wg >/dev/null || { echo "install wireguard-tools first" >&2; exit 1; }
peers=$(dirname "$0")/peers.conf
[ -r "$peers" ] || { echo "peers.conf is missing next to this script" >&2; exit 1; }
out=hl-$name.conf
[ ! -e "$out" ] || { echo "$out exists; refusing to overwrite a key" >&2; exit 1; }

umask 077
key=$(wg genkey)
{
    printf '[Interface]\nPrivateKey = %s\nAddress = %s/32\n' "$key" "$ip"
    sed "s/HOME_ADDRESS/$home/" "$peers"
} > "$out"
echo "Wrote $out (private key inside: never send this file)."
echo "Public key to send to the controller:"
printf '%s\n' "$key" | wg pubkey
