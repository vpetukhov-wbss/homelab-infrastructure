# Runbook — changing the firewall on node4 without losing it

**When:** before `security.yml` or `fail2ban.yml` touches node4. Its SSH (2345, through
the router's forward) is the owner's only remote path, so a wrong rule means a trip to
the keyboard.

Keep the current SSH session open throughout. An established connection survives an
`ufw` change, so it is the second way back in.

## 1. Arm the rollback

~~~bash
sudo systemd-run --unit=homelab-firewall-rollback --on-active=20min /bin/sh -c \
  'ufw --force disable; systemctl disable --now fail2ban;
   rm -f /etc/ssh/sshd_config.d/00-homelab-hardening.conf; systemctl reload ssh'
systemctl list-timers homelab-firewall-rollback.timer    # expect: one timer, ~20 min left
~~~

It returns node4 to a bare key-only sshd, so use it only to get back in. The timer does
not survive a reboot: do not reboot before step 4.

## 2. Apply

~~~bash
cd ~/Projects/homelab-infrastructure/ansible
V=~/homelab/.venv/bin
$V/ansible-playbook playbooks/security.yml --limit node4-laptop --check --diff   # read it first
$V/ansible-playbook playbooks/security.yml --limit node4-laptop
$V/ansible-playbook playbooks/fail2ban.yml --limit node4-laptop
~~~

Expect `failed=0`. The roles assert the live rules, `sshd -T` and the jail.

## 3. Test along the real paths

| From | Check | Expect |
|---|---|---|
| outside (through the router) | a **new** `ssh -p 2345` | logs in |
| node1 (LAN) | `ssh -p 2345` reaches the key exchange | open |
| node1 (LAN) | a temporary listener on node4, any other port | times out |
| node1 (LAN) | one login as an invalid user | `fail2ban-client status sshd`: total failed grows by 1 |
| node4 | `ansible homelab -m ping` | all four answer |

## 4. Keep it

Only after the outside login works:

~~~bash
sudo systemctl stop homelab-firewall-rollback.timer
systemctl list-timers --all | grep -c homelab-firewall-rollback    # expect: 0
~~~

## If you are locked out

Wait for the timer. If it was already cancelled, log in at node4's keyboard and run
`sudo ufw disable`.
