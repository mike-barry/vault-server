# task-003: Vault roots in `vaults.json` are absolute machine paths

**Goal:** A clone at any path gets a working `test-vault` without editing `vaults.json`, and the file stops churning per developer.

## Context

Carried over from `docs/Sidequests.md`, which was retired. The original entry is reproduced in full under Notes; it states what is wrong now, what it costs while it stays, and why the obvious fix does not work.

## Acceptance criteria

Not yet written - see Open questions.

## Open questions

- The entry's fix - resolving a root relative to the configuration file's own directory - is a change to retained code, which it says explicitly. That needs the operator's approval before it lands, and a check of whether the specs cover configuration path resolution. Has that been sought?
- A root that does not exist currently records Degraded and the server starts with the vault quietly unavailable. Is that behaviour in scope to change here, or does it stay as it is once the path resolves correctly?

## Notes

The entry as it stood in `docs/Sidequests.md`, verbatim:

> ## Vault roots in `vaults.json` are absolute machine paths
>
> Approved: 2026-08-02.
>
> The committed entry is `C:/Repos/vault-server-redesign/test-vault`. Any clone at another path gets a vault whose root does not exist, and `VaultConfig` records that as Degraded rather than failing, so the server starts and the vault is quietly unavailable. The file also churns per developer.
>
> The obvious fix is the wrong one. `VaultConfig` resolves a root with `Path.GetFullPath(vaultRoot)`, which resolves against the working directory, and `run.ps1` sets that to `src/VaultMcpServer` - so a relative `test-vault` resolves to `src/VaultMcpServer/test-vault` and fails the same way, with the same quiet degradation. Resolving relative to the configuration file's own directory is what makes a relative spelling mean what it looks like, and that is a change to retained code.
