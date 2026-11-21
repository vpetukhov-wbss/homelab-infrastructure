# Security policy

This repository is public. Treat every committed byte as visible forever.

## Never commit

- passwords, connection strings that contain passwords, API keys, tokens
- SSH, WireGuard or TLS private keys
- the homelab (ansible-vault) password, decrypted ansible-vault files, the recovery bundle
- Vault unseal keys, recovery keys or root tokens
- backup passphrases or keys, age identities
- the real inventory (`inventory/hosts.yml`) and the installer build output (`installer/build/`)

Public keys, placeholders, templates and documentation addresses are fine. `.gitignore`
covers the known paths. It is a safety net, not a control.

## Where secrets live

See [docs/security/secrets-management.md](docs/security/secrets-management.md).

## Network policy

- Databases, the broker, Vault and admin interfaces bind to the WireGuard overlay and
  are never published to the Internet.
- Public traffic terminates at the edge (node1, Caddy).
- Remote administration is meant to go over WireGuard. Until that exists, the laptop's
  SSH port forwarded by the router is a tracked exception.

The verified controls and open gaps are in
[docs/security/posture.md](docs/security/posture.md).

## Checks

- `gitleaks` over the tree and the history before every push. This is not automated
  yet.
- The Claude Code safety hooks (`.claude/hooks/`) block common secret paths and
  destructive commands. They are guardrails, not a sandbox.

## Reporting

If you find a credential or private key in this repository, do not post it in a public
issue. Use GitHub's private vulnerability reporting for this repository.

What the owner does then:

1. Revoke or rotate the secret.
2. Remove it from the tree.
3. Decide on rewriting the history.
