#!/usr/bin/env bash
# Build the encrypted controller recovery bundle: everything that exists only on the
# controller, sealed with the homelab password (~/.homelab_recovery_pass).
#
# Contents: ~/.ssh, ~/.homelab_vault_pass, ~/homelab-backups, ~/homelab (minus the
# venv), ~/homelab-audit, Claude Code project memory, a git bundle of the repository
# (all refs, including unpushed commits) plus a tar of uncommitted and git-ignored
# files (real inventory, ansible-vault files), and optionally /etc/wireguard.
#
# Prints names and counts only — never contents. Restore with installer/homelab-restore.sh
# (the installer stick runs it as `sudo homelab-restore`).
set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: make-recovery-bundle.sh [--etc-wireguard-tar FILE] [--out-dir DIR]

  --etc-wireguard-tar FILE  a tar of /etc/wireguard made as root (root-only on this host)
  --out-dir DIR             where to write the bundle (default ~/homelab-recovery)
EOF
}

H="$HOME"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PASS="$H/.homelab_recovery_pass"
OUT="$H/homelab-recovery"
WG_TAR=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --etc-wireguard-tar) WG_TAR="${2:-}"; shift 2 ;;
    --out-dir) OUT="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done
die() { echo "error: $*" >&2; exit 1; }
[[ -s "$PASS" ]] || die "missing $PASS (run scripts/set-homelab-password.sh)"
for c in gpg git tar; do command -v "$c" >/dev/null || die "missing command: $c"; done

umask 077
STAMP="$(date +%Y%m%d-%H%M)"
mkdir -p "$OUT"; chmod 700 "$OUT"
STAGE="$OUT/.stage-$STAMP"
mkdir -p "$STAGE/repo-extra"
trap 'rm -rf "$STAGE"' EXIT

# 1. Repository: full history (all refs) + what git does not carry.
git -C "$REPO" bundle create "$STAGE/homelab-infrastructure.bundle" --all 2>/dev/null
git -C "$REPO" bundle verify "$STAGE/homelab-infrastructure.bundle" >/dev/null 2>&1 \
  || die "git bundle does not verify"
git -C "$REPO" rev-parse --abbrev-ref HEAD > "$STAGE/BRANCH"
git -C "$REPO" remote get-url origin > "$STAGE/ORIGIN" 2>/dev/null || echo "" > "$STAGE/ORIGIN"
( cd "$REPO" && {
    git ls-files -m -o --exclude-standard
    find inventory -type f ! -name '*.bak'
    find installer/build -maxdepth 1 -type f ! -name '*.iso' 2>/dev/null || true
  } | sort -u | tar -cf "$STAGE/repo-extra/worktree-and-ignored.tar" -T - )
echo "git bundle: $(git -C "$REPO" rev-list --count --all) commits, branch $(cat "$STAGE/BRANCH"); extras: $(tar -tf "$STAGE/repo-extra/worktree-and-ignored.tar" | wc -l) files"

# 2. Optional root-only material handed in as a tar.
if [[ -n "$WG_TAR" ]]; then
  [[ -s "$WG_TAR" ]] || die "$WG_TAR is empty or unreadable"
  cp "$WG_TAR" "$STAGE/etc-wireguard.tar"
  echo "/etc/wireguard: $(tar -tf "$STAGE/etc-wireguard.tar" | grep -vc '/$') file(s)"
fi

# 3. Stream everything into one encrypted file (no plaintext tarball on disk).
paths=(home/vpetukhov/.ssh home/vpetukhov/.homelab_vault_pass)
for p in homelab-backups homelab homelab-audit; do
  [[ -e "$H/$p" ]] && paths+=("home/vpetukhov/$p")
done
# Claude Code: project MEMORY only — not session transcripts (they hold tool output).
for d in "$H"/.claude/projects/*/memory; do
  [[ -d "$d" ]] && paths+=("${d#/}")
done
BUNDLE="$OUT/homelab-recovery-$STAMP.tar.gpg"
tar -C / -cf - \
    --exclude=home/vpetukhov/homelab/.venv \
    "${paths[@]}" \
    --transform "s|^${STAGE#/}|stage|" "${STAGE#/}" 2>/dev/null \
  | gpg --batch --yes --quiet --no-symkey-cache --pinentry-mode loopback --passphrase-file "$PASS" \
        --symmetric --cipher-algo AES256 -o "$BUNDLE"

# 4. Prove it decrypts and holds what a restore needs.
LIST="$(gpg --batch --quiet --no-symkey-cache --pinentry-mode loopback --passphrase-file "$PASS" -d "$BUNDLE" | tar -tf -)"
missing=0
for must in home/vpetukhov/.ssh/homelab-controller home/vpetukhov/.homelab_vault_pass \
            stage/homelab-infrastructure.bundle stage/repo-extra/worktree-and-ignored.tar \
            stage/BRANCH stage/ORIGIN; do
  if grep -qx "$must" <<<"$LIST"; then echo "  present  $must"; else echo "  MISSING  $must"; missing=1; fi
done
[[ "$missing" -eq 0 ]] || { rm -f "$BUNDLE"; die "bundle incomplete - removed"; }
( cd "$OUT" && sha256sum "$(basename "$BUNDLE")" > "$(basename "$BUNDLE").sha256" )
chmod 600 "$OUT"/*
echo "bundle : $BUNDLE ($(du -h "$BUNDLE" | cut -f1), $(wc -l <<<"$LIST") entries)"
echo "Copy it OFF this machine (and keep the password somewhere else)."
