# Terraform

Terraform owns declarative infrastructure state and provider-managed resources.

Planned areas include:

- Vault configuration and policies where appropriate
- DNS / Cloudflare resources
- identity relationships and CI/CD integrations
- infrastructure metadata derived from inventory
- remote-state configuration

Terraform is not the primary OS configuration engine. Avoid large remote-exec provisioners. Host configuration belongs in Ansible.

## State

Terraform state is sensitive and must never be committed. The remote-state backend will be selected and documented before real apply workflows are enabled.

## Workflow

Typical lifecycle:

1. terraform fmt
2. terraform validate
3. terraform plan
4. human review
5. approved terraform apply
6. Ansible configuration
7. health validation
