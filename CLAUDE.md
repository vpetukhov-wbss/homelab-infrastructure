# CLAUDE.md

## Read this first

**[`docs/PROJECT-CONTEXT.md`](docs/PROJECT-CONTEXT.md) is the handover document.**
Read it before proposing or changing anything. It covers:

- the actual hardware and the limits it imposes;
- why each service sits where it does, and what was cut;
- the incidents so far;
- the traps that each produced a system that reported success while achieving nothing.

Three things from it are load-bearing:

- **The hardware is small.** Three servers have 2 cores, ~3 GB of usable RAM and
  spinning disks; node4 (the laptop) is the only machine with an SSD, AVX and 7 GB.
  Disk I/O is the ceiling. Containers solve version isolation, not throughput.
- **Docker's port publishing bypasses ufw.** What keeps a service private is the bind
  address plus the overlay guard, not the firewall.
- **Verify effective state, not intended configuration.** A green Ansible run proves
  only that the role ran. Use `sshd -T`, not `sshd -t`. Test exposure along the
  attacker's path. Reboot to prove that something persists.

## Goal

A self-hosted platform on four machines for one owner:

- replicated PostgreSQL and Redis, RabbitMQ, SQLite, SFTP, Gitea with Actions CI, and
  application hosting behind one edge;
- on node4: MongoDB, Vaultwarden, LocalStack and monitoring;
- reachable from anywhere by the owner over WireGuard, and by nobody else;
- encrypted backups with tested restores;
- every machine rebuildable from the installer stick and one password.

The controller (Ansible and Claude Code) lives on node4. Milestones and the decisions
still open are in [`docs/roadmap.md`](docs/roadmap.md).

The repository is public on GitHub and doubles as the owner's portfolio. It must stay
truthful: never describe planned work as deployed.

## Working here

1. Read the relevant files **and the live state** before changing anything. Host and
   container names do not reliably show current roles, so query the service.
2. Identify affected nodes, dependencies, network paths, stateful data and the rollback.
3. Plan first for multi-node, security, database, networking or destructive changes.
4. Reuse existing roles. Keep them idempotent and make them assert their own effect.
5. Run the quality gates below before calling anything done.
6. Update the documents that own what changed (see Documentation).
7. Report what changed, what was validated, the residual risk and the next step.

Specialist subagents live in `.claude/agents/`. Use them when a task crosses domains:

| Task | Agent |
|---|---|
| placement, capacity, topology | `platform-architect` |
| Ubuntu, SSH, systemd, storage, Docker host | `linux-sysadmin` |
| WireGuard, firewall, Caddy/nginx, TLS, exposure | `network-security-engineer` |
| PostgreSQL, Redis/Sentinel, RabbitMQ, MongoDB, replication and failover | `data-platform-sre` |
| Ansible roles, inventory, installer and recovery tooling | `iac-engineer` |
| Gitea, Gitea Actions, runners, pipelines | `cicd-engineer` |
| monitoring, incidents, backup/restore, DR | `sre-operations` |
| secrets, exposure, privilege review | `security-reviewer` |
| roadmap, sequencing, acceptance criteria | `technical-project-manager` |

## Non-negotiable safety rules

- **Secrets in chat or on disk.** Never generate, print, commit or persist a real secret
  unless the owner supplies a secure destination and it is necessary. The owner reads
  passwords in their own terminal.
- **Never commit:**
  - Vault unseal/recovery material;
  - WireGuard, SSH or TLS private keys;
  - the homelab (vault) password or decrypted ansible-vault files;
  - the real inventory, tokens or passwords.
- **Exposure.** Never expose databases, Vault, Gitea admin, Grafana or the Docker API
  to the Internet. Administration goes over WireGuard. The one standing exception, the
  laptop's SSH on the router-forwarded port 2345 (the owner's only remote path today),
  is a tracked gap that WireGuard remote access closes.
- **Firewall.** The default posture is deny inbound.
- **Disruptive actions need the owner's go-ahead,** with the impact and rollback stated
  first: wiping disks, resetting databases, rotating credentials, failover, rebooting a
  data node.
- **Git.** Commit or push only when asked; never `git push --force` to a shared branch.
  Commits and PRs carry **no AI attribution**: no `Co-Authored-By: Claude` trailer and no
  "Generated with Claude Code" line. The owner is the author. This overrides any tool
  default that asks for such lines. AI use is acknowledged once, openly, by the "Built
  with Claude Code" badge in the README.
- **Controls stay on.** Never disable a security control to make a service work.
- **Backups.** Replication is not a backup, and a backup is incomplete until its restore
  is documented and tested.

## Tools and their boundaries

| Tool | Owns | Status |
|---|---|---|
| `installer/` + `scripts/build-installer-iso.py` | bare metal to a reachable host (Ubuntu autoinstall stick) | built for node4 |
| Ansible (`ansible/`) | everything on a host: packages, users, SSH, firewall, WireGuard, Docker, services | all three servers |
| Docker Compose | service runtime, templated by the Ansible roles | every service |
| ansible-vault | the secrets in use today (`inventory/group_vars/homelab/*.yml`, git-ignored) | in use |
| HashiCorp Vault | the intended runtime secret store | initialised 2026-11-21, holds no secrets yet; re-seals on restart |
| Gitea + Actions | internal Git and CI | running, no pipelines yet |
| GitHub | public upstream and portfolio; `main` is the current state | push over SSH alias `github-homelab` (deploy key for this repository only) |
| `scripts/` | thin entry points; logic that belongs in a role goes in the role | |

**Not used:**

- **Terraform:** nothing provider-managed exists yet; propose it only together with
  such a resource, for example DNS for TLS.
- **Jenkins, Kubernetes:** see `docs/PROJECT-CONTEXT.md` §6.
- **Microsoft SQL Server:** declined by the owner; do not re-propose it.

## Architecture principles

- Prefer boring, well-understood technology. KISS, DRY, YAGNI.
- Logical roles are independent of hosts. Discover the current primary or master at run
  time and never hardcode it.
- Do not claim HA without the quorum, failover and storage to support it. Prefer manual
  failover over unsafe automatic failover.
- A stateful service needs explicit persistence, a backup and a restore runbook.
- Every new service states its CPU, RAM and disk cost against `PROJECT-CONTEXT.md` §2.

## Repository layout

- `ansible/`: roles and playbooks; `playbooks/site.yml` runs them all in order
- `inventory/`: example inventory, secret-variable template and image pins (the real
  inventory and secrets are git-ignored)
- `installer/`: autoinstall template and restore tool; build output in `installer/build/`
  (git-ignored)
- `scripts/`: installer build/write, recovery bundle, homelab password rotation
- `docs/`: handover, architecture, security, roadmap, runbooks
- `.claude/`: agents, skills, rules, safety hooks, and `docs/` (working notes Claude loads
  on demand, indexed in `.claude/docs/INDEX.md`: today the HomeLab Pulse UI plan and data
  mapping)
- `apps/`: application code (HomeLab Pulse: the monitoring agent and its contracts)

## Quality gates

Run whichever apply. Never claim a check passed unless it ran.

- **Ansible:** from `ansible/`, `ansible-playbook --syntax-check playbooks/<file>.yml`,
  plus `ansible-lint`
- **YAML:** `yamllint`
- **Shell:** `shellcheck`
- **Secrets:** `gitleaks` before any push
- **Live:** the role's own assertions; for an exposure change, a test along the
  attacker's path (for example a LAN route to the overlay)

## Documentation

README is the entry point. When behaviour changes, update the document that owns it:

| Change | Update |
|---|---|
| a service added, moved or exposed | `docs/architecture/overview.md` (service catalog) and `docs/PROJECT-CONTEXT.md` |
| a security control or gap | `docs/security/posture.md` |
| where a secret lives or how it is recovered | `docs/security/secrets-management.md` |
| an operational procedure | `docs/runbooks/` |
| a milestone done or a decision made | `docs/roadmap.md` |
| a trap that cost debugging time | `docs/PROJECT-CONTEXT.md` §7 |
| the Pulse UI plan, its screens, or the telemetry-to-widget mapping | `.claude/docs/pulse-ui*.md` (index: `.claude/docs/INDEX.md`) |

Everywhere, distinguish deployed, planned and cut.

## Definition of done

A container starting is not done. Done means all of the following:

- reproducible from the repository, with secrets handled properly;
- the intended exposure verified along the attacker's path;
- persistence and health checks in place;
- a backup and a restore path;
- the documents updated;
- failure and rollback behaviour understood.

## Skills

`/plan-change`, `/bootstrap-node`, `/deploy-node`, `/service-onboard`, `/security-review`,
`/incident-response`, `/dr-drill`, `/review-infrastructure`, `/project-status`. See
`docs/claude-code.md`.
