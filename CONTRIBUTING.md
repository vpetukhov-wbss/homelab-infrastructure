# Contributing

This is a working homelab first and a public reference second. A change follows the
same path whether a person or Claude Code makes it.

## Workflow

1. Branch from `main`, and give each commit one purpose (`feat(ansible): …`,
   `fix(…): …`, `docs: …`).
2. Change the role, not the host. Avoid manual fixes that the repository cannot
   reproduce.
3. Run the checks below.
4. Apply the smallest playbook that covers the change, and read the role's own
   assertions in its output.
5. Update the documents that own the behaviour (see the table in `CLAUDE.md` under
   Documentation).
6. Get the owner's explicit go-ahead, with the rollback stated, for anything
   destructive or disruptive: wipes, failovers, credential rotation, reboots of data
   nodes.

## Checks

~~~bash
cd ansible
for p in playbooks/*.yml; do ~/homelab/.venv/bin/ansible-playbook --syntax-check "$p" || break; done
cd ..
~/homelab/.venv/bin/ansible-lint ansible/playbooks ansible/roles   # config: .ansible-lint
yamllint -d relaxed ansible inventory/*.example.yml inventory/group_vars/all
shellcheck scripts/*.sh installer/*.sh
gitleaks git --pre-commit --staged --redact .    # before a commit: what is staged
gitleaks git --redact .                          # before a push: the whole history
~~~

Do not point `gitleaks dir` at the working copy: it also scans git-ignored files (the
real inventory, the installer build with the Wi-Fi PSK) and prints what it finds.
Reviewed false positives go into `.gitleaksignore` by fingerprint, with a comment.

Never claim that a check passed unless it ran.

## What a new service needs

Use `/service-onboard`. The service needs all of the following:

- a role and a playbook (added to `site.yml`);
- a bind on the overlay, plus a test along the attacker's path;
- a pinned image;
- a backup entry and a restore runbook if it holds state;
- a row in the service catalog (`docs/architecture/overview.md`);
- its cost against the hardware in `docs/PROJECT-CONTEXT.md` §2.
