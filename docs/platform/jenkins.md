# Jenkins

Jenkins is the CI/CD and operational orchestration layer.

## Responsibilities

- validate Terraform and Ansible
- lint YAML/shell/configuration
- run security/secret scans
- build and test applications
- build container images
- produce Terraform plans
- require approval for sensitive applies
- invoke approved Ansible deployment workflows
- execute smoke/health validation
- schedule operational jobs where appropriate

## Placement

Initial placement: Management/App node.

Future placement: dedicated DevOps Control Plane node with Gitea and isolated build agents.

## Security

Jenkins is not intended to be directly public. Administrative access should traverse the secure gateway/WireGuard path. Credentials should be sourced from Vault or short-lived identities wherever practical.
