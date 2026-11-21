# Runbook — initialise Vault

**When:** once, before anything stores secrets in Vault. **Who:** a human. The unseal keys
and root token are produced exactly once; an agent must never see or store them.

**Why it is urgent:** whoever runs `operator init` on a reachable, uninitialised Vault
owns it. Until init, the edge role publishes Vault on `127.0.0.1` only
(`vault_publish_ip`), so only someone already on node1 can do it.

## Preconditions

~~~bash
ssh -p 2222 vpetukhov@<node1>
sudo docker exec vault vault status -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
# Expect: Initialized false, Sealed true
~~~

If it says `Initialized true` and you did not do it: **treat it as a compromise.** Vault
holds nothing yet, so stop it, move `~/stacks/edge/vault-data` aside, start it again and
initialise.

## Initialise

~~~bash
sudo docker exec -it vault vault operator init \
  -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
~~~

The output contains **5 unseal keys and the initial root token**. Store them offline
right now — a password manager *and* paper. Never in the repository, a chat, a ticket,
or a file on node1. Losing three keys means losing everything Vault will ever hold.

## Unseal (3 of the 5 keys)

~~~bash
for i in 1 2 3; do
  sudo docker exec -it vault vault operator unseal -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
done
sudo docker exec vault vault status -address=https://127.0.0.1:8200 -ca-cert=/vault/tls/vault.crt
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
