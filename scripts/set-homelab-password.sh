#!/usr/bin/env bash
# Replace the homelab master password (ansible-vault + recovery bundle) with one you choose.
#
# Prompts twice without echo, re-keys every ansible-vault file to the new password on
# temporary copies, and proves each copy decrypts with the new password to exactly the
# same bytes as the original. Only then are the new files staged next to the originals
# and renamed into place, and the password files updated. If anything fails before the
# renames, nothing has changed. Plaintext only ever passes through a pipe into sha256sum.
# Run it in your own terminal: the password is never printed or logged.
set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: set-homelab-password.sh

Re-keys these ansible-vault files to a new password you type (min 12 characters):
  <repo>/inventory/**   ~/homelab/inventory/**   ~/homelab-backups/**
and writes it to ~/.homelab_vault_pass and ~/.homelab_recovery_pass (mode 600).
Tip: 3-4 words plus a number is easy to remember and hard to guess.
EOF
}
[[ "${1:-}" == "-h" || "${1:-}" == "--help" ]] && { usage; exit 0; }

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VAULT_BIN="${ANSIBLE_VAULT_BIN:-$HOME/homelab/.venv/bin/ansible-vault}"
OLD_PASS="$HOME/.homelab_vault_pass"
REC_PASS="$HOME/.homelab_recovery_pass"
die() { echo "error: $*" >&2; exit 1; }

[[ -x "$VAULT_BIN" ]] || die "ansible-vault not found at $VAULT_BIN"
[[ -s "$OLD_PASS" ]] || die "$OLD_PASS missing - cannot open the current vault files"
[[ -t 0 ]] || die "run this in an interactive terminal"

# shellcheck disable=SC2016  # '$ANSIBLE_VAULT' is the literal file header, not a variable
mapfile -t FILES < <(grep -rlZ --null '^\$ANSIBLE_VAULT' "$REPO/inventory" "$HOME/homelab/inventory" "$HOME/homelab-backups" 2>/dev/null | tr '\0' '\n' | sort -u)
[[ ${#FILES[@]} -gt 0 ]] || die "no ansible-vault files found"

# IFS= : keep exactly what was typed (plain `read` silently trims surrounding spaces).
IFS= read -rsp "New homelab password (min 12 characters): " P1; echo
IFS= read -rsp "Repeat: " P2; echo
[[ "$P1" == "$P2" ]] || die "the two entries differ - nothing changed"
[[ ${#P1} -ge 12 ]] || die "too short (${#P1}) - nothing changed"
# ansible strips surrounding whitespace from password files, gpg does not: refuse it.
[[ "$P1" != [[:space:]]* && "$P1" != *[[:space:]] ]] || die "no spaces at the start or end, please - nothing changed"

umask 077
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; unset P1 P2' EXIT
printf '%s' "$P1" > "$WORK/new"

# sha256 of a vault file's plaintext. Works for binary payloads (the WireGuard tar);
# `--output /dev/null` does not: ansible-vault refuses to write into /dev.
plain_sha() {  # $1 = password file, $2 = vault file
  local out
  out="$("$VAULT_BIN" decrypt --vault-password-file "$1" --output - "$2" </dev/null 2>/dev/null | sha256sum)" || return 1
  printf '%s' "${out%% *}"
}

i=0
for f in "${FILES[@]}"; do
  i=$((i + 1)); cp -p "$f" "$WORK/$i"
  before="$(plain_sha "$OLD_PASS" "$f")" \
    || die "$f does not open with the current password - nothing changed"
  "$VAULT_BIN" rekey --vault-password-file "$OLD_PASS" --new-vault-password-file "$WORK/new" "$WORK/$i" </dev/null >/dev/null 2>&1 \
    || die "could not re-key $f - nothing changed"
  after="$(plain_sha "$WORK/new" "$WORK/$i")" \
    || die "re-keyed copy of $f does not open with the new password - nothing changed"
  [[ "$before" == "$after" ]] || die "re-keyed copy of $f differs from the original - nothing changed"
  echo "ok  $f"
done

# Stage everything next to its destination first (same filesystem), so the swap below
# is a series of renames that cannot run out of space halfway.
STAGED=()
cleanup_staged() { rm -f -- "${STAGED[@]}"; }
i=0
for f in "${FILES[@]}"; do
  i=$((i + 1))
  cp -p "$WORK/$i" "$f.rekey.$$" || { cleanup_staged; die "cannot write next to $f - nothing changed"; }
  STAGED+=("$f.rekey.$$")
done
for p in "$OLD_PASS" "$REC_PASS"; do
  install -m 600 "$WORK/new" "$p.rekey.$$" || { cleanup_staged; die "cannot write $p - nothing changed"; }
  STAGED+=("$p.rekey.$$")
done

done_files=0
for f in "${FILES[@]}"; do
  if ! mv -f -- "$f.rekey.$$" "$f"; then
    cleanup_staged
    die "rename failed at $f after $done_files file(s): those open with the NEW password, the rest still with the old one (still in $OLD_PASS)"
  fi
  done_files=$((done_files + 1))
done
mv -f -- "$OLD_PASS.rekey.$$" "$OLD_PASS"
mv -f -- "$REC_PASS.rekey.$$" "$REC_PASS"

echo "Done: ${#FILES[@]} file(s) re-keyed; ~/.homelab_vault_pass and ~/.homelab_recovery_pass updated."
echo "Write the password down somewhere safe (password manager or paper)."
