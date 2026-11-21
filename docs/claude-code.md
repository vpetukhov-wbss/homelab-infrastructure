# Claude Code in this repository

Claude Code runs on the controller, which is node4 once the laptop is reinstalled. It
works on this repository the way a small infrastructure team would. The shared setup is
committed; personal settings and credentials are not.

This repository is built with Claude Code, and the README badge says so openly. Every
commit is authored by the owner and carries no AI co-author line (see `CLAUDE.md`).

## Structure

| Path | Purpose |
|---|---|
| `CLAUDE.md` | Always loaded: goal, safety rules, tool boundaries, quality gates, which document owns what |
| `.claude/rules/` | Conventions scoped to paths (Ansible, compose templates, scripts, docs, security) |
| `.claude/agents/` | Specialist subagents with their own context |
| `.claude/skills/` | Reusable workflows, invoked as `/skill-name` |
| `.claude/hooks/` | Deterministic guardrails that run before tools (see below) |

## Specialists

| Agent | Responsibility |
|---|---|
| `platform-architect` | topology, placement, capacity, HA claims |
| `linux-sysadmin` | Ubuntu, systemd, storage, SSH, Docker hosts |
| `network-security-engineer` | WireGuard, firewall and overlay guard, Caddy/nginx, TLS, exposure |
| `data-platform-sre` | PostgreSQL, Redis/Sentinel, RabbitMQ, SQLite, MongoDB, backup and restore |
| `iac-engineer` | Ansible roles and inventory, the installer and recovery tooling |
| `cicd-engineer` | Gitea, Gitea Actions and act_runner, GitHub publishing |
| `sre-operations` | monitoring, incidents, backups, DR drills, runbooks |
| `security-reviewer` | review before merge or deployment |
| `technical-project-manager` | roadmap, sequencing, acceptance criteria |

## Skills

~~~text
/plan-change            plan before multi-node, security, data or destructive work
/bootstrap-node         bare metal → installer stick → reachable, managed host
/deploy-node            converge one node with Ansible and verify the live state
/service-onboard        add a service end to end (placement → backup → catalog row)
/security-review        review staged changes
/incident-response      diagnose and stabilise an outage
/dr-drill               prove a restore, with measured RPO/RTO
/review-infrastructure  a review from several disciplines at once
/project-status         evidence-based status against docs/roadmap.md
~~~

## Safety hooks

`.claude/settings.json` registers two `PreToolUse` hooks. They need `bash` and `jq`.

- `safety-gate.sh` blocks a small set of clearly destructive shell patterns: force
  push, `git reset --hard`, `rm -rf` at the root, `mkfs`/`wipefs`/raw disk writes,
  Docker prune with volumes, disabling the firewall.
- `protected-path-gate.sh` blocks reading or writing common secret, key and state paths.

They are guardrails, not a sandbox. The real protection comes from key-only SSH,
firewalls, the overlay guard, ansible-vault, backups and human approval of disruptive
steps.

## Memory

Claude Code keeps project memory under `~/.claude/projects/<project>/memory/`. It holds
the owner's working preferences, not secrets. The recovery bundle carries it, so a
reinstalled controller remembers how the owner works.
