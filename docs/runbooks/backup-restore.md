# Runbook — restore from a backup

## Where the backups are

| Copy | Location |
|---|---|
| Primary | node1 `/home/vpetukhov/backups/homelab-YYYYMMDD-HHMMSS.tar.gz.enc` (newest 21) |
| Second disk | node3 `/srv/homelab-sync/data/backups/` (pruned after 30 days by node3) |

Nightly at 00:00 UTC (`homelab-backup.timer`, node1). node3 snapshots its own SQLite and
Gitea at 23:40 UTC (`homelab-exports.timer`) for the job to pull. A failed or partial
run fails the systemd unit: check `systemctl --failed` and
`journalctl -u homelab-backup` on node1.

## Decrypt

On node1, as root (the passphrase file is there):

~~~bash
mkdir -p /root/restore && cd /root/restore
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass file:/etc/homelab/backup.pass \
  -in /home/vpetukhov/backups/<archive> | tar -xz
ls -R
~~~

Elsewhere, the passphrase is `vault_backup_passphrase` in the ansible-vault files; feed
it with `-pass stdin` rather than putting it on a command line.

## What is inside, and how each part goes back

| File | Restore |
|---|---|
| `postgres-globals.sql` | **First**, roles and their passwords: `psql -h <primary> -U postgres -f postgres-globals.sql` |
| `postgres.dump` | Into the **primary**: `pg_restore -h <primary> -U postgres -d homelab --clean --if-exists postgres.dump` |
| `redis.rdb` | Stop Redis, start it with `appendonly no` and this file as `/data/dump.rdb`, then `CONFIG SET appendonly yes` — with AOF on, Redis ignores the RDB |
| `rabbitmq-definitions.json` | `rabbitmqctl import_definitions rabbitmq-definitions.json` (topology only, not messages) |
| `node3-exports/sqlite/*` | Stop the app, copy into `~/stacks/apphost/sqlite/` on node3 |
| `node3-exports/gitea-dump.zip` | Gitea's own dump: repositories, database, `app.ini` (holds the Actions secret key). Follow Gitea's "restore from dump" steps for 1.22 |
| `sftp-tree.tar` | Into node1's `~/stacks/edge/sftp_data/` (owner 1001:1001) |
| `etc-wireguard.tar` | node1's `/etc/wireguard` (private keys) — only when rebuilding node1 |

Run every restore into the **current primary/master** — discover them first
(`redis-failover-and-reboots.md`), never assume node2.

## Drill (do this; an untested backup is a guess)

At least monthly, into throwaway containers, not production:

~~~bash
pg_restore --list postgres.dump | head          # the dump is readable
docker run --rm -v "$PWD":/r redis:7.4.12-alpine redis-check-rdb /r/redis.rdb
python3 -m zipfile -t node3-exports/gitea-dump.zip
for f in node3-exports/sqlite/*.db; do sqlite3 "$f" 'PRAGMA integrity_check'; done
~~~

The new archive format (with Gitea, SFTP and WireGuard) has **not** been restore-drilled
end to end yet.
