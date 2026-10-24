# Ansible

Ansible owns machine and service configuration.

Planned reusable roles:

- base
- users
- security
- firewall
- docker
- wireguard
- node_exporter
- caddy
- nginx
- postgres
- mongodb
- redis
- sqlserver
- vault
- jenkins
- gitea
- sftp
- restic
- prometheus
- grafana
- localstack

Roles should be idempotent, composable and driven by inventory/group variables rather than copied per host.

A full site playbook will eventually allow a new node to progress from bootstrap Ubuntu to its assigned role with one controlled workflow.
