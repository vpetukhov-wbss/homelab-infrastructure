# Claude Code Project Setup

This repository ships a shared Claude Code configuration so Claude operates like a small infrastructure engineering team rather than a generic coding assistant.

## Structure

- `CLAUDE.md`: always-on mission, safety rules, boundaries, team routing.
- `.claude/rules/`: global and path-scoped conventions.
- `.claude/agents/`: specialist subagents with isolated contexts.
- `.claude/skills/`: reusable operational workflows exposed as slash skills.

The root file intentionally stays focused. Long procedures live in skills and load only when needed.

## Specialist team

| Agent | Responsibility |
|---|---|
| platform-architect | topology, HA, service placement, capacity |
| linux-sysadmin | Ubuntu, systemd, storage, host troubleshooting |
| network-security-engineer | WireGuard, routing, DNS, firewall, proxies, TLS |
| data-platform-sre | PostgreSQL, SQL Server, MongoDB, Redis, recovery |
| iac-engineer | Terraform, Ansible, inventory, bootstrap |
| cicd-engineer | Jenkins, Gitea, pipelines, agents, releases |
| sre-operations | monitoring, incidents, backups, DR |
| security-reviewer | infrastructure security review |
| technical-project-manager | roadmap, dependencies, acceptance criteria |

Claude can delegate automatically when a request matches an agent description. You can also request one explicitly.

## Skills

~~~text
/plan-change
/bootstrap-node
/deploy-node
/service-onboard
/security-review
/incident-response
/dr-drill
/review-infrastructure
/project-status
~~~

Typical flow:

~~~text
/plan-change Implement PostgreSQL primary/replica with backup and restore
~~~

Then implement in reviewable slices and finish with:

~~~text
/review-infrastructure
/security-review
~~~

Outage:

~~~text
/incident-response PostgreSQL replica stopped receiving WAL
~~~

Recovery validation:

~~~text
/dr-drill PostgreSQL full-node-loss scenario
~~~

## Safety philosophy

Prompt rules are not a replacement for deterministic controls. Real infrastructure should also use protected branches, Jenkins approvals, least-privilege credentials, Vault policies, host firewalls, restricted sudo, secret scanning, backup retention, and tested restores.

Hooks/permissions can be added later after they are tested on the actual admin workstation. This avoids an untested blocking rule that breaks legitimate operations or creates false confidence.

## Deterministic safety hooks

The project also contains `.claude/settings.json` with PreToolUse hooks.

- Bash operations pass through a small destructive-command gate.
- Read/Write/Edit operations pass through a protected-path gate for common secret/key/state files.

These hooks require Bash and `jq` on the workstation running Claude Code. Claude Code requires review of project hook changes before they take effect.

The hooks are intentionally conservative and small. They do not replace least-privilege accounts, sudo policy, Vault, firewalls, branch protection, CI approvals, or operator judgment.

See `.claude/hooks/README.md`.

## Local/private configuration

Do not commit Claude credentials, API keys, MCP secrets, private inventory, or machine-specific secret settings. Project agents/skills/rules are shared; personal preferences belong in user-level Claude configuration.
