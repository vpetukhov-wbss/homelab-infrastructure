# Runbook — promote the PostgreSQL standby

**When:** the primary (node2) is gone for long enough that having no writable database
is worse than the work of re-seeding it later. Not for a node that is merely slow or
briefly unreachable: two primaries is the outcome to avoid at any cost.

**Who:** a human. There is no automatic PostgreSQL failover, by design.

## 1. Fence the old primary

It must not come back as a second primary. In order of preference:

- it is powered off and stays off until step 5, or
- `sudo docker stop postgres-primary` on node2, if it is reachable, **and**
- nobody runs `playbooks/data-primary.yml` until step 5 — that playbook starts a primary
  on node2 unconditionally.

`restart: unless-stopped` brings a primary back on node2's next boot, so if node2 is
simply off, leave it off or unplug its network until you reach step 5.

## 2. Promote node3

~~~bash
sudo docker exec -u postgres postgres-replica psql -XAtc "select pg_promote()"
sudo docker exec -u postgres postgres-replica psql -XAtc "select pg_is_in_recovery()"
# Expect: t, then f
~~~

node3's `pg_hba` already accepts application connections from the overlay and a
replication connection from node2 (until 2026-11-21 it did not, and a promotion would
have left the applications unable to connect).

## 3. Point the clients

- The backup job needs nothing: it probes both nodes and refuses to run if it finds two
  primaries.
- Applications must be pointed at `10.44.0.3:5432`.

## 4. Prepare node3 to have a standby

~~~bash
sudo docker exec -u postgres postgres-replica psql -XAtc \
  "select pg_create_physical_replication_slot('node2_slot')"
~~~

## 5. Bring node2 back as a standby

`wal_log_hints = on` makes `pg_rewind` possible, but with a database this size a fresh
copy is simpler and has fewer ways to go wrong:

1. On node2: `docker compose -f ~/stacks/data-primary/docker-compose.yml stop postgres-primary`.
2. Move its data volume aside (do not delete it until node2 streams again).
3. `pg_basebackup -h 10.44.0.3 -U replicator -D <empty data dir> -Fp -Xs -S node2_slot -R`
   (in a one-off `postgres` container, as the replica role does for node3).
4. Start it; it comes up as a standby of node3.

The Ansible roles still describe node2 as primary. Until they are generalised, **swap
the `data_primary`/`data_replica` groups in the inventory and review both roles before
running either playbook again.**

## Verify

~~~bash
# on node3 (new primary)
sudo docker exec -u postgres postgres-replica psql -XAtc "select application_name, state from pg_stat_replication"
# Expect: a row in state 'streaming'
~~~

Then write a row on node3 and read it on node2.
