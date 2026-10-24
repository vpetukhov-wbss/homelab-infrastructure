---
name: plan-change
description: Plan a HomeLab infrastructure change before implementation, especially multi-node, networking, security, database, CI/CD, or disruptive work.
---
# Plan infrastructure change
1. Read relevant architecture, inventory, rules, and service docs.
2. State outcome and affected nodes/services/files.
3. Map dependencies and traffic/data flows.
4. Identify stateful data and backup requirements.
5. Identify security/exposure changes.
6. Define failure and rollback path.
7. Assign work to Terraform, Ansible, Docker, Jenkins, or docs.
8. Define validation.
9. Produce ordered steps with hold points for destructive actions.
