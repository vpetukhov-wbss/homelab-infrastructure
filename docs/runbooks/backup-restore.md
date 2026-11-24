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

`backup.yml` runs the job once. More than 6 h after the nightly export that run ends with
`node3 exports are stale` and an error, although the archive is complete: start
`homelab-exports.service` on node3 first.

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
| `rabbitmq-definitions.json` | Copy it into the container, then `rabbitmqctl import_definitions <file>` (topology only, not messages). From a file: on standard input it waits forever |
| `node3-exports/sqlite/*` | Stop the app, copy into `~/stacks/apphost/sqlite/` on node3 |
| `node3-exports/gitea-dump.zip` | Gitea's own dump: repositories, database, `app.ini` (holds the Actions secret key). Into an empty `/data`: the zip's `data/` becomes `/data/gitea`, `app.ini` goes to `/data/gitea/conf/`, `repos/` to `/data/git/repositories/`; delete the copied `gitea.db` and load `gitea-db.sql` into a new one; `chown -R 1000:1000`. The drill does exactly this |
| `mongodb.archive.gz` | Once MongoDB is deployed; see [below](#mongodb) |
| `sftp-tree.tar` | Into node1's `~/stacks/edge/sftp_data/` (owner 1001:1001) |
| `etc-wireguard.tar` | node1's `/etc/wireguard` (private keys) — only when rebuilding node1 |

Run every restore into the **current primary/master** — discover them first
(`redis-failover-and-reboots.md`), never assume node2.

## MongoDB

Not deployed yet; this was tried with MongoDB 7.0 on node4 (2026-11-24). Run as root on
node4, from the directory that holds the decrypted archive. The password goes in a file,
never on the command line, and `--entrypoint` matters: the image's own drops to uid 999.

~~~bash
C=$(mktemp); read -rsp 'MongoDB root password: ' P; printf 'password: "%s"\n' "$P" > "$C"; unset P
docker run --rm --network host --entrypoint mongorestore -v "$C:/run/m.yaml:ro" -v "$PWD:/r:ro" \
  <mongodb_image from inventory/group_vars/all/images.yml> \
  --config /run/m.yaml --host 10.44.0.4 --username root --authenticationDatabase admin \
  --gzip --archive=/r/mongodb.archive.gz --nsInclude '<database>.*'
rm -f "$C"
~~~

Expect `N document(s) restored successfully. 0 document(s) failed to restore.` Add `--drop`
to replace existing collections. For the whole instance at the dump's end point, leave out
`--nsInclude` and add `--oplogReplay`; that variant has not been tried.

## Drill (about monthly; an untested backup is a guess)

From `ansible/` on node4:

~~~bash
~/homelab/.venv/bin/ansible-playbook playbooks/restore-drill.yml
~~~

It takes the newest archive from **node3's copy** (the one left if node1 is lost), checks
that node1's copy has the same checksum, and restores it on node4 into throwaway
containers without a network. Nothing is written to a production service. The decrypted
data lives only in a RAM mount that cannot be swapped out. Expect one `ok` line per part
and `RESULT: everything restored`; any other result fails the play.

| Step | What passes |
|---|---|
| `archive` | newer than 26 h, decrypts, every expected part present, **no part without a step below** |
| `postgres` | globals and dump restore without errors; tables and rows as in the dump; the vault's password works and a wrong one does not; the replication role exists |
| `redis` | keys as in the snapshot, and still there after a restart from the AOF |
| `rabbitmq` | every archived definition is in the broker, password hashes included; the vault's password works |
| `sqlite` | `integrity_check` of every database |
| `gitea` | starts from the SQL dump and is healthy; users and repositories as in the dump; `git fsck` of every repository |
| `sftp` | unpacks, owned by 1001:1001 |
| `wireguard` | the archived private key belongs to the peer node1 uses now |

**Last run: 2026-11-24**, archive of 00:09 UTC the same day, everything restored in 20 s.
PostgreSQL, RabbitMQ, SFTP and SQLite matched the live counts. Two limits:

- the platform holds almost no data yet, and Redis and Gitea were empty at the source. Both
  were therefore also tried with a synthetic archive (500 keys; one user, one repository);
- the time is for 53 KB with the images already pulled, so it is not a recovery-time
  estimate for a real load.

The drill itself was tested the same day with a wrong passphrase, wrong service passwords,
a truncated and an old archive, an unknown and a missing part, damaged PostgreSQL, Redis,
SQLite and Gitea data, and a foreign WireGuard key: each one failed on the right step. A
run killed halfway cleaned up after itself; after `kill -9` the mount stays until
`umount /run/homelab-restore-drill`, and the next run refuses to start.

Not covered: Vault (no raft snapshot in the backup yet), MongoDB (not deployed; its dump
will fail the drill until it gets a step), and rebuilding a machine.
