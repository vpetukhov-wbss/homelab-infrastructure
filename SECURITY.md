# Security Policy

This repository is public. Treat every committed byte as Internet-visible forever.

## Never commit secrets

Do not commit:

- passwords or connection strings containing passwords
- API keys or access tokens
- SSH private keys
- WireGuard private keys
- Vault root tokens, recovery keys or unseal material
- TLS private keys
- SOPS age private identities
- backup encryption passwords
- Terraform state or plan files containing secrets
- environment files containing real credentials

Public keys, example values, templates and documentation-only network addresses are acceptable.

## Runtime secret source

HashiCorp Vault is the intended runtime source of truth. GitHub Secrets should be limited to CI bootstrap cases where short-lived identity or OIDC cannot be used.

## Repository controls

Planned controls include gitleaks, pre-commit checks, CI secret scanning, dependency updates and security scanning.

## Network policy

Administrative interfaces and databases are private by default. WireGuard is the preferred administrative/backend transport. Public exposure must terminate at the designated gateway and be explicitly documented.

## Reporting

If you discover a credential, private key or other sensitive value in this repository, do not reproduce it in a public issue. Rotate/revoke the credential first, then remove it from the current tree and address repository history where appropriate.
