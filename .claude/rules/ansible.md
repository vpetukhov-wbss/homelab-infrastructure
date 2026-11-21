---
paths:
  - "ansible/**"
  - "**/playbooks/**"
  - "**/roles/**"
---
# Ansible rules

- Roles must be idempotent.
- Use modules instead of shell/command where suitable modules exist.
- Shell/command tasks need a reason and correct changed semantics.
- Keep defaults safe and environment-neutral.
- Secrets come from the ansible-vault files in inventory/group_vars/homelab/ (git-ignored), never plaintext vars; `no_log` on tasks that handle them.
- Use handlers for service restarts caused by config changes.
- Validate service config before restart when possible.
- Prefer inventory groups and role composition over duplicated host playbooks.
- Support check mode where practical.
- Assert the effective live state (e.g. `sshd -T`, listening addresses, replication), not just that a task ran.
- Tag disruptive tasks clearly.
