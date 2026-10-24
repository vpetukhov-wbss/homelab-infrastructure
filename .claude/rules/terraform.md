---
paths:
  - "terraform/**"
  - "**/*.tf"
  - "**/*.tfvars.example"
---
# Terraform rules

- Keep modules small and purpose-specific.
- Pin provider/version constraints intentionally.
- Never commit real tfvars or state.
- Do not use Terraform provisioners for general OS configuration when Ansible is appropriate.
- Prefer explicit variables/outputs over hidden coupling.
- Remember sensitive values can still exist in state.
- Run `terraform fmt` and `terraform validate` before completion.
- Produce and review a plan before apply.
- Never default to `-auto-approve`.
- Surface destructive replacements clearly before execution.
