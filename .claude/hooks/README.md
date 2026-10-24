# Claude Code Safety Hooks

Project-level hooks provide deterministic guardrails in addition to prompt instructions.

## Enabled hooks

### safety-gate.sh

Runs before Bash commands and blocks a small set of clearly destructive patterns, including:

- terraform destroy
- terraform apply -auto-approve
- force push
- git reset --hard
- root-level rm -rf
- mkfs / wipefs / raw block-device writes
- Docker prune including volumes
- disabling/flushing host firewalls

It intentionally does not attempt to classify every possible shell command.

### protected-path-gate.sh

Runs before Read/Write/Edit and blocks access to common local secret/private-key/state paths.

## Requirements

The hooks use Bash and jq. Install jq on the workstation running Claude Code.

Hooks execute with the user's permissions. Review hook configuration in Claude Code before enabling it.

## Design principle

These hooks are guardrails, not a complete sandbox. Real protection still comes from least-privilege OS accounts, sudo policy, Vault, firewall rules, protected Git branches, CI approvals, backups, and careful operator review.
