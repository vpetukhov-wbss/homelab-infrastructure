#!/usr/bin/env bash
set -Eeuo pipefail

INPUT="$(cat)"
if ! command -v jq >/dev/null 2>&1; then
  echo "Claude safety hook requires jq; allowing command because the hook cannot inspect it." >&2
  exit 0
fi

CMD="$(printf '%s' "$INPUT" | jq -r '.tool_input.command // ""')"
LOWER="$(printf '%s' "$CMD" | tr '[:upper:]' '[:lower:]')"

block() {
  echo "Blocked by HomeLab Claude safety gate: $1" >&2
  echo "Review the impact and run the operation manually only when explicitly intended." >&2
  exit 2
}

[[ "$LOWER" =~ terraform[[:space:]]+destroy ]] && block "terraform destroy requires explicit human execution."
[[ "$LOWER" =~ terraform[[:space:]]+apply.*-auto-approve ]] && block "terraform apply -auto-approve bypasses the review gate."
[[ "$LOWER" =~ git[[:space:]]+reset[[:space:]]+--hard ]] && block "git reset --hard can discard work."
[[ "$LOWER" =~ git[[:space:]]+push.*--force ]] && block "force-pushing shared history is prohibited."
[[ "$LOWER" =~ git[[:space:]]+push.*[[:space:]]-f([[:space:]]|$) ]] && block "force-pushing shared history is prohibited."
[[ "$LOWER" =~ rm[[:space:]]+-rf[[:space:]]+/([[:space:]]|$|*) ]] && block "recursive deletion from filesystem root is prohibited."
[[ "$LOWER" =~ (^|[[:space:];|])mkfs([[:space:].]|$) ]] && block "filesystem formatting is destructive."
[[ "$LOWER" =~ (^|[[:space:];|])wipefs([[:space:]]|$) ]] && block "filesystem signature wiping is destructive."
[[ "$LOWER" =~ (^|[[:space:];|])dd[[:space:]].*of=/dev/ ]] && block "raw writes to block devices are destructive."
[[ "$LOWER" =~ docker[[:space:]]+system[[:space:]]+prune.*--volumes ]] && block "Docker prune with volumes can delete persistent data."
[[ "$LOWER" =~ (^|[[:space:];|])ufw[[:space:]]+disable([[:space:]]|$) ]] && block "disabling the host firewall violates the security model."
[[ "$LOWER" =~ iptables[[:space:]]+-f([[:space:]]|$) ]] && block "flushing firewall rules can expose services."
[[ "$LOWER" =~ nft[[:space:]]+flush[[:space:]]+ruleset ]] && block "flushing the nftables ruleset can expose services."

exit 0
