# Secrets Management

## Source of truth

HashiCorp Vault is the intended runtime secret store.

Examples:

- database credentials
- application credentials
- API tokens
- deployment credentials
- internal service credentials
- generated dynamic credentials where supported

## GitHub

GitHub is public source control for this project. Real runtime secrets do not belong in the repository.

GitHub Secrets may be used only for workflow bootstrap cases where workload identity/OIDC or Vault cannot reasonably remove the static credential.

## SOPS + age

SOPS with age may be introduced for encrypted bootstrap or disaster-recovery material that must be versioned.

Rules:

- only encrypted values may be committed
- age private identities remain offline/outside Git
- recovery copies belong on encrypted media
- SOPS is not a replacement for Vault runtime secret management

## Terraform state

Terraform state can contain sensitive values even when configuration marks them sensitive. State must not be committed.

The final remote-state backend will be selected during implementation. State access must be authenticated, encrypted and backed up.

## WireGuard

Private keys should be generated on the node whenever practical. Public keys can be distributed by automation. Private keys must not appear in the public inventory.

## Secret incident procedure

If a secret is accidentally committed:

1. revoke/rotate it immediately
2. replace affected credentials
3. remove it from the current tree
4. evaluate repository history cleanup
5. review logs for unauthorized use
6. add a detection/prevention rule so the same class of mistake is harder to repeat
