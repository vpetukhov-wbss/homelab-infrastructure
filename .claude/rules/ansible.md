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
- Secrets come from Vault/secure injection, not plaintext vars.
- Use handlers for service restarts caused by config changes.
- Validate service config before restart when possible.
- Prefer inventory groups and role composition over duplicated host playbooks.
- Support check mode where practical.
- Tag disruptive tasks clearly.
