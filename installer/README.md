# Bare-Metal Installer

The installer layer creates a generic Ubuntu Server 24.04 LTS host. It must not permanently encode application or database roles.

## Planned responsibilities

- unattended Ubuntu installation
- OpenSSH server
- bootstrap administrative user
- SSH public-key authentication
- Python for Ansible
- curl/ca-certificates and minimal bootstrap packages
- security updates
- predictable disk setup
- first-boot readiness marker

## Explicitly not installed here

PostgreSQL, SQL Server, MongoDB, Redis, Vault, Jenkins, Gitea, Caddy and application workloads belong to later Terraform/Ansible-controlled layers.

## Goal

One installer image should be able to bootstrap any physical node. Role assignment happens from the repository inventory after the OS is online.
