# Security rules

- Treat every committed byte as permanently public.
- Never commit real secrets, tokens, passwords, private keys, Terraform state, or recovery material.
- Prefer placeholders or secure runtime references.
- Administrative/backend services are private by default.
- Public ingress terminates at the designated edge/gateway.
- Prefer WireGuard for node administration and backend traffic.
- Any new exposed port must be documented with source, destination, purpose, protocol, and firewall scope.
- Avoid privileged containers; document any unavoidable exception and compensating controls.
- Prefer least privilege, read-only mounts, dropped capabilities, and explicit resource limits.
- Never weaken authentication, TLS, firewalling, or host-key verification as a convenience fix.
- Security-sensitive changes require validation and rollback/recovery considerations.
