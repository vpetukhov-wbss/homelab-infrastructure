---
name: deploy-node
description: Deploy or converge a HomeLab node safely from inventory using Terraform/Ansible and validate the result.
---
# Deploy/converge node
Resolve node/role from inventory; confirm dependencies; run static validation; produce/review Terraform plan where applicable; apply with approval; run smallest Ansible scope; verify systemd/Docker health and intended listening interfaces; verify monitoring; for stateful roles verify persistence and replication; report evidence and rollback.
