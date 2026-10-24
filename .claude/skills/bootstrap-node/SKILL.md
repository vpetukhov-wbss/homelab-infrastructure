---
name: bootstrap-node
description: Prepare or review the repeatable bare-metal-to-managed-node workflow using Ubuntu Autoinstall, SSH bootstrap, inventory, Terraform, and Ansible.
---
# Bootstrap node
Verify hardware and role; use generic Ubuntu 24.04 Autoinstall; install bootstrap SSH key/user with password SSH off; establish temporary LAN reachability; run base Ansible; configure hostname, updates, firewall, Docker prerequisites and WireGuard; verify overlay; restrict management access; apply role composition; validate services/monitoring/persistence/restart; update inventory/docs. Never bake real secrets or permanent role logic into public installer media.
