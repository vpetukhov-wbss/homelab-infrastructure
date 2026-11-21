# Runbook — controlled Redis failover and rolling reboots

**When:** before rebooting or maintaining the node that holds the Redis master, and for
kernel/libc updates (`/var/run/reboot-required`).

Failover is a deliberate decision: it briefly interrupts writes and changes which node
is master. Do it with someone watching.

## Who is who right now

~~~bash
# on any node; Sentinel requires the Redis password
export REDISCLI_AUTH='<vault_redis_password>'      # do not paste it into chats or logs
sudo -E docker exec -e REDISCLI_AUTH redis-sentinel redis-cli -p 26379 sentinel get-master-addr-by-name homelab-redis
sudo -E docker exec -e REDISCLI_AUTH redis-sentinel redis-cli -p 26379 sentinel ckquorum homelab-redis
# Expect: the master's overlay IP; "OK 3 usable Sentinels"
~~~

Container names do not tell you the role — `redis-replica` on node3 can be the master.

## Controlled failover (move the master away from a node)

~~~bash
sudo -E docker exec -e REDISCLI_AUTH redis-sentinel redis-cli -p 26379 sentinel failover homelab-redis
# Expect: OK, then within ~10 s get-master-addr-by-name returns the other node
~~~

Verify on both data nodes (`role:master` on one, `role:slave` + `master_link_status:up`
on the other):

~~~bash
sudo docker exec <redis-container> redis-cli info replication | grep -E '^(role|master_host|master_link_status|connected_slaves)'
sudo grep '^replicaof' ~/stacks/*/redis/redis.conf   # the replica's file now names the new master
~~~

The `replicaof` line on disk is what makes a later restart come back in the right role.

## Rolling reboot order

Never more than one node at a time — two Sentinels down means no failover at all.

| Order | Node | Before | Impact while it is down |
|---|---|---|---|
| 1 | node1 | — | Edge (Caddy, Gitea UI, app), RabbitMQ, Vault (re-seals!) for ~1.5 min |
| 2 | node3 | Fail Redis away if node3 is master | Read-only PostgreSQL standby, Gitea, app host |
| 3 | node2 | Fail Redis away if node2 is master; **remove the Windows USB stick** | **No PostgreSQL writes** for ~1.5 min |

~~~bash
cd ansible
ansible <node> -b -m ansible.builtin.reboot -a "reboot_timeout=900 post_reboot_delay=30"
~~~

## After each reboot — what must be true

~~~bash
uname -r                                           # the new kernel
sudo iptables -t raw -S HOMELAB-OVERLAY-GUARD      # guard present
ip route show type unreachable                     # 10.44.0.0/24 metric 4000
cat /sys/class/watchdog/watchdog0/state            # active
sudo wg show wg1 latest-handshakes                 # both peers recent
sudo docker ps -a                                  # every container Up
systemctl --failed                                 # nothing
~~~

Plus, for data nodes: PostgreSQL standby `streaming` (`select status from
pg_stat_wal_receiver`), Redis role and link as above, Sentinel `ckquorum` OK.

## If a node does not come back

Power-cycle it by hand. Redis keeps serving from the other node (the remaining two
Sentinels still have quorum). **Do not promote PostgreSQL** just because node2 is late —
see `postgres-promotion.md`; waiting was the right call on 2026-11-18.
