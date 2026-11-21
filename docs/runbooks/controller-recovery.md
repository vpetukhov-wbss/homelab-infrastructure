# Runbook — controller recovery

The controller is wherever Ansible (and Claude Code) runs. It holds the only copies of:

| What | Why it matters |
|---|---|
| `~/.ssh/homelab-controller` | SSH is key-only: without it, no remote login to any node |
| `~/.homelab_vault_pass` | Opens every ansible-vault file, including the WireGuard backup |
| `inventory/hosts.yml`, `inventory/group_vars/homelab/*.yml` | Real inventory and secrets; git-ignored |
| `~/.ssh/github-homelab-infrastructure` + the `github-homelab` entry in `~/.ssh/config` | The deploy key that pushes this repository to GitHub; replaceable in the repository's Settings → Deploy keys |

Lose these and recovery means a monitor and keyboard at every node.

## Make a bundle (after every meaningful change)

~~~bash
scripts/make-recovery-bundle.sh
# root-only material, e.g. this machine's /etc/wireguard:
sudo tar -C /etc -cf /root/etc-wireguard.tar wireguard && sudo install -m 600 -o "$USER" /root/etc-wireguard.tar ~/etc-wireguard.tar
scripts/make-recovery-bundle.sh --etc-wireguard-tar ~/etc-wireguard.tar && shred -u ~/etc-wireguard.tar
~~~

Output: `~/homelab-recovery/homelab-recovery-YYYYMMDD-HHMM.tar.gpg` (+ `.sha256`),
encrypted with the homelab password, verified by decrypting and listing it.

**Copy it to at least two places that are not this machine.** Keep the password
somewhere else (password manager *and* paper). Never in `/tmp`: on Ubuntu it is tmpfs,
which is how the 2026-11-20 bundle was lost.

To change the homelab password to one you can remember:
`scripts/set-homelab-password.sh` (re-keys every vault file; run it in your own terminal).

## Restore on a fresh machine

**With the node installer stick** (it carries the bundle): log in after the install and
run `sudo homelab-restore`. It asks for the homelab password once and restores the SSH
identity, the vault password, the repository with full history plus the git-ignored
inventory, the WireGuard backups, Claude Code's project memory, and recreates the Ansible
virtualenv.

**By hand:**

~~~bash
gpg -d homelab-recovery-*.tar.gpg | tar -x -C ~/restore
cat ~/restore/stage/RESTORE.txt    # older bundles
git clone ~/restore/stage/homelab-infrastructure.bundle ~/Projects/homelab-infrastructure
git -C ~/Projects/homelab-infrastructure checkout "$(cat ~/restore/stage/BRANCH)"
tar -xf ~/restore/stage/repo-extra/worktree-and-ignored.tar -C ~/Projects/homelab-infrastructure
install -m 600 ~/restore/home/vpetukhov/.homelab_vault_pass ~/.homelab_vault_pass
cp -a ~/restore/home/vpetukhov/.ssh/. ~/.ssh/ && chmod 700 ~/.ssh && chmod 600 ~/.ssh/homelab-controller
~~~

## Verify

~~~bash
cd ~/Projects/homelab-infrastructure/ansible
~/homelab/.venv/bin/ansible homelab -m ping            # host keys are pinned: no prompts
~/homelab/.venv/bin/ansible-vault view ../inventory/group_vars/homelab/vault.yml >/dev/null && echo "vault opens"
~~~
