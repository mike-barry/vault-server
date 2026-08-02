# Sidequests

Work the operator has agreed should be done, but which does not block the build. Real problems, noticed in passing, parked deliberately rather than forgotten.

## How to use it

- **Only the operator adds an entry.** An agent that finds something belonging here says so and proposes it; it does not write to this file on its own. That rule is what keeps the file a list of agreed work rather than a place to shed anything inconvenient.
- **The bar is that it is genuinely worth doing and genuinely not blocking.** Something that blocks a build step is a step in `Plan.md`. Something the specs cannot answer is an escalation in `Questions.md`. Something nobody has agreed to is not recorded anywhere.
- **An entry says what is wrong now, not just what to change.** The cost of leaving it is what lets the operator schedule it against everything else.

Entry shape:

```
## <The item, as work to be done>

Approved: date.
What is wrong now, and what it costs while it stays.
What the fix touches, and what makes it less obvious than it looks.
```

## Vault roots in `vaults.json` are absolute machine paths

Approved: 2026-08-02.

The committed entry is `C:/Repos/vault-server-redesign/test-vault`. Any clone at another path gets a vault whose root does not exist, and `VaultConfig` records that as Degraded rather than failing, so the server starts and the vault is quietly unavailable. The file also churns per developer.

The obvious fix is the wrong one. `VaultConfig` resolves a root with `Path.GetFullPath(vaultRoot)`, which resolves against the working directory, and `run.ps1` sets that to `src/VaultMcpServer` - so a relative `test-vault` resolves to `src/VaultMcpServer/test-vault` and fails the same way, with the same quiet degradation. Resolving relative to the configuration file's own directory is what makes a relative spelling mean what it looks like, and that is a change to retained code.
