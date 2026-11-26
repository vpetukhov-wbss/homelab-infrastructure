#!/bin/sh
# Create the first Gitea admin and show its one-time password. Gitea has no user and
# registration is off, so nobody can sign in until this has run once. Gitea asks for a
# new password at the first login.
#
# Run it on node4 in a tmux window of its own (Ctrl-b c), never in the one that runs
# Claude Code, and do not paste its output anywhere:
#   sh scripts/gitea-first-admin.sh [username]      (default: your login name)
set -eu
user=${1:-$(id -un)}
case $user in *[!a-z0-9_.-]*|'') echo "username: a-z, 0-9, _ . -" >&2; exit 2 ;; esac
clear
ssh -n -o BatchMode=yes -i "$HOME/.ssh/homelab-controller" -o HostKeyAlias=node3-data \
  "$(id -un)@192.168.0.103" \
  docker exec -u git gitea gitea admin user create --admin --username "$user" \
  --email "$user@homelab.invalid" --random-password --must-change-password=true \
  --config /data/gitea/conf/app.ini
echo
printf 'Write the password down, then press Enter to wipe this screen. '
read -r _
clear
tmux clear-history 2>/dev/null || true
