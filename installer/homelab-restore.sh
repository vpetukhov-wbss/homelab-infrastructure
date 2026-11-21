#!/bin/bash
# homelab-restore — rebuild the homelab controller on this machine from the encrypted
# recovery bundle the installer copied to /var/lib/homelab-restore.
#
#   sudo homelab-restore
#
# Restores, for the user who ran sudo: SSH keys/config/known_hosts (the installer's
# authorized_keys is kept), the ansible-vault password, the repository with its full
# history plus uncommitted and git-ignored files (real inventory, vault files), the
# WireGuard backups, and recreates the Ansible virtualenv. Nothing is overwritten
# without saying so; nothing secret is printed.
set -Eeuo pipefail

BUNDLE=/var/lib/homelab-restore/recovery.tar.gpg
DONE_MARKER=/var/lib/homelab-restore/restored
die() { echo "error: $*" >&2; exit 1; }

[[ "$(id -u)" -eq 0 ]] || die "run it with sudo:  sudo homelab-restore"
[[ -f "$BUNDLE" ]] || die "no recovery bundle at $BUNDLE"
[[ -t 0 ]] || die "run it in an interactive terminal (it asks for the password)"

U="${SUDO_USER:-}"
[[ -n "$U" && "$U" != root ]] || U="$(getent passwd 1000 | cut -d: -f1)"
H="$(getent passwd "$U" | cut -d: -f6)"
[[ -n "$U" && -d "$H" ]] || die "could not determine the target user"
as_user() { sudo -u "$U" -H "$@"; }

W="$(mktemp -d)"; chmod 700 "$W"
trap 'rm -rf "$W"' EXIT

echo "Restoring the homelab controller for user '$U' (home $H)."
echo "Enter the homelab password (the one set with scripts/set-homelab-password.sh):"
gpg --pinentry-mode loopback --no-symkey-cache --quiet --decrypt "$BUNDLE" | tar -x -C "$W" \
  || die "could not decrypt the bundle (wrong password?) - nothing was changed"
SRC="$W/home/vpetukhov"
STAGE="$W/stage"
[[ -d "$SRC" && -d "$STAGE" ]] || die "unexpected bundle layout - nothing was changed"

# 1. SSH identity. authorized_keys stays as the installer wrote it.
install -d -m 700 -o "$U" -g "$U" "$H/.ssh"
for f in "$SRC"/.ssh/*; do
  n="$(basename "$f")"
  case "$n" in authorized_keys) continue ;; *.pub) m=644 ;; *) m=600 ;; esac
  install -m "$m" -o "$U" -g "$U" "$f" "$H/.ssh/$n"
done
echo "  ssh keys, config, known_hosts .......... restored"

# 2. The ansible-vault password (same value as the homelab password).
install -m 600 -o "$U" -g "$U" "$SRC/.homelab_vault_pass" "$H/.homelab_vault_pass"
echo "  ~/.homelab_vault_pass .................. restored"

# 3. Side directories: WireGuard config backup, old Ansible workspace, audit notes.
for d in homelab-backups homelab homelab-audit; do
  [[ -d "$SRC/$d" ]] || continue
  [[ -e "$H/$d" ]] && mv "$H/$d" "$H/$d.before-restore.$(date +%s)"
  cp -a "$SRC/$d" "$H/$d"; chown -R "$U:$U" "$H/$d"
  echo "  ~/$d ....... restored"
done

# 4. Claude Code project memory (settings and credentials are NOT restored: log in again).
if [[ -d "$SRC/.claude/projects" ]]; then
  install -d -m 700 -o "$U" -g "$U" "$H/.claude"
  cp -a "$SRC/.claude/projects" "$H/.claude/"; chown -R "$U:$U" "$H/.claude"
  echo "  Claude Code project memory ............. restored"
fi

# 5. The repository: full history from the git bundle, then the files git does not carry.
R="$H/Projects/homelab-infrastructure"
if [[ -e "$R" ]]; then
  echo "  $R exists - left untouched"
else
  UT="$(as_user mktemp -d)"
  install -m 600 -o "$U" -g "$U" "$STAGE/homelab-infrastructure.bundle" "$UT/repo.bundle"
  install -d -o "$U" -g "$U" "$H/Projects"
  as_user git clone -q "$UT/repo.bundle" "$R"
  as_user git -C "$R" checkout -q "$(cat "$STAGE/BRANCH")"
  as_user git -C "$R" remote set-url origin "$(cat "$STAGE/ORIGIN")"
  as_user tar -xf "$STAGE/repo-extra/worktree-and-ignored.tar" -C "$R"
  rm -rf "$UT"
  echo "  repository + inventory + vault files ... restored ($(as_user git -C "$R" rev-list --count HEAD) commits on $(cat "$STAGE/BRANCH"))"
fi

# 6. The old laptop's /etc/wireguard (road-warrior wg0). Copied, NOT enabled: its address
#    collides with node1's wg0 and needs a decision first.
if [[ -f "$STAGE/etc-wireguard.tar" ]]; then
  install -d -m 700 /etc/wireguard/restored-from-laptop
  tar -xf "$STAGE/etc-wireguard.tar" -C /etc/wireguard/restored-from-laptop --strip-components=1
  chmod -R go-rwx /etc/wireguard/restored-from-laptop
  echo "  WireGuard configs ...................... /etc/wireguard/restored-from-laptop (not enabled)"
fi

# 7. Tooling: git + the Ansible virtualenv the playbooks expect.
apt-get install -y -qq git python3-venv >/dev/null
if [[ ! -x "$H/homelab/.venv/bin/ansible-playbook" ]]; then
  echo "  creating the Ansible virtualenv (needs network) ..."
  as_user python3 -m venv "$H/homelab/.venv"
  as_user "$H/homelab/.venv/bin/pip" install -q --upgrade pip ansible
fi
echo "  Ansible ................................ $(as_user "$H/homelab/.venv/bin/ansible" --version | head -1)"

# 8. Optional: Claude Code.
read -r -p "Install Claude Code for $U now? [Y/n] " answer
case "$answer" in
  n*|N*) echo "  Claude Code ............................ skipped" ;;
  *) as_user bash -c 'curl -fsSL https://claude.ai/install.sh | bash' \
       && echo "  Claude Code ............................ installed (run: claude)" \
       || echo "  Claude Code install failed - retry later: curl -fsSL https://claude.ai/install.sh | bash" ;;
esac

touch "$DONE_MARKER"
cat <<EOF

Done. Check it:
  cd $R/ansible && ~/homelab/.venv/bin/ansible homelab -m ping
Then bring this machine into the platform (overlay 10.44.0.4, firewall, fail2ban) —
see installer/README.md, section "After the restore".
EOF
