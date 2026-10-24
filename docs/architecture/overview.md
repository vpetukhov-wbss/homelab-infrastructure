# Architecture Overview

## Design intent

The HomeLab is modeled as a small platform with explicit edge, data, compute, management and CI/CD responsibilities.

Physical hosts are replaceable implementation details. Logical roles are the stable abstraction.

## Role topology

- Edge/Gateway: controlled ingress, WireGuard, TLS, Caddy, load balancing.
- Data Primary: PostgreSQL primary, MongoDB primary, Redis primary.
- Data Replica/Backup: replicas, SFTP and backup repository.
- Management/App: application runtime, NGINX, Vault member, Portainer and monitoring.
- SQL Primary: dedicated Microsoft SQL Server primary.
- SQL Secondary: dedicated Microsoft SQL Server secondary.
- Future DevOps Control Plane: Gitea, Jenkins controller and build agents/runners.

The exact mapping to hardware remains provisional until machine specifications are recorded.

## Network layers

### Underlay

Normal switched Ethernet/LAN provides basic reachability.

### Overlay

WireGuard provides the trusted encrypted backend network. Application and administration policies should prefer overlay addresses.

### Edge

The gateway is the intentional external entry point. Public application traffic can be reverse-proxied from Caddy to internal NGINX/application endpoints. Administrative services remain private.

## Data resiliency

PostgreSQL, MongoDB, Redis and SQL Server will each receive a service-specific replication/failover design. Automatic failover is enabled only where quorum and failure semantics are understood.

Backups remain independent from replicas.

## Control plane

Terraform describes infrastructure state and provider-managed resources. Ansible configures machines and services. Jenkins orchestrates CI/CD and operational workflows. Gitea provides self-hosted SCM while GitHub remains the public upstream repository.

## Future expansion

The architecture reserves capacity for a dedicated DevOps node. Moving Gitea/Jenkins/runners there should require inventory and deployment changes, not redesign of the platform.
