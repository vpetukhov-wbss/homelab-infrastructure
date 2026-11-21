# Runbook — initialise Vault

**When:** once, before anything stores secrets in Vault. **Who:** a human. The unseal keys
and root token are produced exactly once; an agent must never see or store them.

**Why it is urgent:** whoever runs `operator init` on a reachable, uninitialised Vault
owns it. Until init, the edge role publishes Vault on `127.0.0.1` only
(`vault_publish_ip`), so only someone already on node1 can do it.

## Preconditions

~~~bash
ssh -p 2222 -i ~/.ssh/homelab-controller -o HostKeyAlias=node1-gateway vpetukhov@<node1>
docker exec vault vault status -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
# Expect: Initialized false, Sealed true
~~~

The admin user is in the `docker` group on node1. Elsewhere, prefix `sudo`.

If it says `Initialized true` and you did not do it: **treat it as a compromise.** Vault
holds nothing yet, so stop it, move `~/stacks/edge/vault-data` aside, start it again and
initialise.

## Initialise

**One owner, one unseal key.** Shamir key shares exist to split trust between several
people. With a single owner they only multiply what must be kept safe, so this lab
uses one key, written down twice and kept in two places. With several operators, use
the defaults instead (`-key-shares=5 -key-threshold=3`, then unseal with three keys).

~~~bash
docker exec -it vault vault operator init -key-shares=1 -key-threshold=1 \
  -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
~~~

The output contains **the unseal key and the initial root token**. Do not copy it
anywhere — not into a chat with an assistant either: write it down, then run `clear`. Store both offline
right now, in a password manager *and* on paper. Never put them in the repository, a
chat, a ticket, or a file on node1. Losing the unseal key means losing everything Vault
will ever hold.

## Unseal

~~~bash
docker exec -it vault vault operator unseal -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
# paste the unseal key at the hidden prompt
docker exec vault vault status -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
# Expect: Initialized true, Sealed false
~~~

Vault **re-seals on every restart**, including node1 reboots: after each, unseal again.

## Then

1. Publish it to the overlay: in the inventory set `vault_publish_ip: "{{ wg_overlay_ip }}"`
   for `node1-gateway`, run `playbooks/edge.yml`, and confirm with
   `ss -tln | grep 8200` → `10.44.0.1:8200`.
2. Add a raft snapshot to the backup job (`vault operator raft snapshot save`).
3. Revoke the initial root token once an admin policy and identity exist.

## Rollback

Before any secret is stored, a bad init is undone by stopping the container, removing
`vault-data` and initialising again. After secrets are stored there is no rollback —
only the raft snapshot in the backups.
