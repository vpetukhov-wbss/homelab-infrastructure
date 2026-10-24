# Roadmap

## Phase 1 — Foundation

- [x] Public repository
- [x] High-level architecture
- [x] Security and secret-handling policy
- [x] Terraform / Ansible / Jenkins boundaries
- [x] Gitea selected for self-hosted SCM
- [x] Future DevOps node reserved
- [ ] Record exact hardware inventory
- [ ] Finalize role-to-hardware mapping

## Phase 2 — Bare-metal bootstrap

- [ ] Ubuntu Server 24.04 Autoinstall
- [ ] Generic installer USB workflow
- [ ] bootstrap user and SSH key
- [ ] first-boot discovery
- [ ] base validation

## Phase 3 — Base host configuration

- [ ] Ansible base role
- [ ] host hardening
- [ ] Docker role
- [ ] WireGuard mesh/overlay
- [ ] firewall policy
- [ ] unattended upgrades
- [ ] node exporter

## Phase 4 — Data platform

- [ ] PostgreSQL primary/replica
- [ ] MongoDB replica set
- [ ] Redis replication/Sentinel
- [ ] SQL Server primary/secondary
- [ ] replication validation
- [ ] failover runbooks

## Phase 5 — Platform services

- [ ] Vault
- [ ] Jenkins
- [ ] Gitea
- [ ] Portainer
- [ ] LocalStack
- [ ] SFTP
- [ ] Prometheus/Grafana

## Phase 6 — Application hosting

- [ ] Caddy edge
- [ ] NGINX internal proxies
- [ ] load balancing
- [ ] application deployment pattern
- [ ] health checks
- [ ] optional Cloudflare Tunnel

## Phase 7 — Backups and disaster recovery

- [ ] Restic repository
- [ ] PostgreSQL backup/restore
- [ ] MongoDB backup/restore
- [ ] Redis recovery
- [ ] SQL Server backup/restore
- [ ] Vault backup/snapshot procedure
- [ ] config backup
- [ ] external/offline backup copy
- [ ] scheduled recovery drills

## Phase 8 — DevOps control plane

- [ ] dedicate additional node
- [ ] migrate Gitea
- [ ] migrate Jenkins controller
- [ ] isolate build agents
- [ ] add internal artifact/container registry if justified
