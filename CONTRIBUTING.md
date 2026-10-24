# Contributing

This project is both a working HomeLab and a public infrastructure reference.

## Principles

- Prefer simple, repeatable automation.
- Keep roles reusable and idempotent.
- Follow KISS, DRY and YAGNI.
- Do not embed machine-specific secrets in reusable roles.
- Separate public examples from environment-specific values.
- Document operational consequences, not just commands.
- Every stateful service must eventually have backup and restore documentation.
- Avoid one-off manual fixes that cannot be reproduced from the repository.

## Change workflow

1. Create a focused branch.
2. Update documentation when architecture changes.
3. Run formatting/linting/validation.
4. Review Terraform plans before apply.
5. Require explicit approval for destructive infrastructure changes.
6. Validate service health after deployment.

## Commit scope

Prefer small commits with a clear purpose: bootstrap, networking, database, monitoring, security, documentation or CI/CD.
