---
name: security-review
description: Perform a structured security review of proposed or staged HomeLab changes before merge/deployment.
---
# Security review
Review secrets/state/key leakage, exposure/firewall scope, SSH/WireGuard/TLS/auth, container privilege/mounts, Docker socket, Jenkins/Gitea permissions, Vault policies, database binding/auth, dependency pinning, destructive commands, and backup/recovery impact. Use the security-reviewer agent where useful. Output severity, evidence, impact, minimal remediation, residual risk, and pass/pass-with-notes/block recommendation.
