#!/usr/bin/env bash
set -Eeuo pipefail

INPUT="$(cat)"
if ! command -v jq >/dev/null 2>&1; then
  echo "Claude protected-path hook requires jq; allowing operation because the hook cannot inspect it." >&2
  exit 0
fi

TOOL="$(printf '%s' "$INPUT" | jq -r '.tool_name // ""')"
PATH_VALUE="$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // .tool_input.path // ""')"

[ -z "$PATH_VALUE" ] && exit 0

NORMALIZED="$(printf '%s' "$PATH_VALUE" | tr '\\' '/' | tr '[:upper:]' '[:lower:]')"
BASE="$(basename "$NORMALIZED")"

block() {
  echo "Blocked by HomeLab protected-path gate: $1" >&2
  echo "Tool: $TOOL | Path: $PATH_VALUE" >&2
  exit 2
}

case "$BASE" in
  .env|.env.*|terraform.tfstate|terraform.tfstate.*|vault-init.json|id_rsa|id_ed25519|keys.txt)
    block "sensitive files must not be read or modified by the shared project agent."
    ;;
esac

[[ "$NORMALIZED" == *"/secrets/"* ]] && block "secrets directories are outside the shared agent boundary."
[[ "$NORMALIZED" == *"/private/"* ]] && block "private credential directories are outside the shared agent boundary."
[[ "$NORMALIZED" == *"unseal-key"* || "$NORMALIZED" == *"recovery-key"* ]] && block "Vault recovery material is protected."
[[ "$NORMALIZED" == *"wireguard/privatekey"* || "$NORMALIZED" == *"wg-private"* ]] && block "WireGuard private keys are protected."
[[ "$NORMALIZED" == *.pem || "$NORMALIZED" == *.p12 || "$NORMALIZED" == *.pfx ]] && block "private certificate/key material is protected."

exit 0
