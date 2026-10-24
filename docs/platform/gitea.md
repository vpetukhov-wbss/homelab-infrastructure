# Gitea

Gitea is the selected self-hosted source-control platform for the HomeLab.

## Purpose

- host private/internal repositories
- provide a Git service that continues to work inside the lab
- integrate with Jenkins
- optionally mirror selected GitHub repositories
- exercise self-hosted SCM administration

GitHub remains the public upstream for this infrastructure project.

## Placement

Initial placement: Management/App node with conservative resource limits.

Future placement: dedicated DevOps Control Plane node together with Jenkins and build agents when hardware becomes available.

## Security

- no direct public administration interface
- access through WireGuard / gateway policy
- persistent database/repository volumes
- backup repositories and Gitea configuration
- credentials sourced from Vault
