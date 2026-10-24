# CLAUDE.md

## Project purpose

This repository defines a production-inspired self-hosted HomeLab using Ubuntu Server, Terraform, Ansible, Docker, WireGuard, Jenkins, Gitea, Vault, replicated data services, monitoring and automated recovery.

## Engineering rules

- Never generate or commit real secrets.
- Never place Terraform state in Git.
- Never hard-code private keys, tokens or passwords.
- Use example values and clearly named placeholders.
- Prefer role-based configuration over node-specific duplication.
- Keep Terraform focused on infrastructure state and provider resources.
- Keep Ansible focused on OS/service configuration.
- Keep Jenkins focused on CI/CD and operational orchestration.
- Prefer idempotent automation.
- Follow KISS, DRY and YAGNI.
- Avoid unnecessary helpers, wrappers and abstraction layers.
- Do not introduce Kubernetes until Docker-based architecture is stable and there is a demonstrated need.
- Default network posture is deny inbound.
- Administrative/backend communication should use WireGuard.
- Public services must enter through the designated gateway.
- Database ports must never be intentionally public.
- Any privileged container requires an explicit documented reason.
- Replication is not a backup.
- Every stateful service must gain a tested restore runbook.

## Repository structure

Keep infrastructure concerns separated:

- installer: generic bare-metal bootstrap
- inventory: role and node declarations
- terraform: declarative infrastructure/provider resources
- ansible: reusable configuration roles/playbooks
- docs: architecture, security, operations and runbooks
- docker: service stacks
- vault: policies/auth/templates, never real secrets
- monitoring: metrics and dashboards
- scripts: thin operational entry points only

## Hardware binding

Do not assume final hardware placement until the inventory contains verified CPU, RAM, storage and NIC information. Logical roles should remain portable between physical machines.

## Documentation

README should remain the high-level entry point. Detailed procedures belong under docs/. Prefer diagrams and copy-pasteable examples, but clearly distinguish example networks/hostnames from real environment values.
