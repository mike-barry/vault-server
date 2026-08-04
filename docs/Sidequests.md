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

## The audit surface: a content-redaction flag, and a CLI audit log

Approved: 2026-08-03.

Every server call's full arguments land verbatim in `logs/` - since `edit` and
`create` shipped, that includes note bodies and frontmatter operations - and the
CLI writes no audit line at all, so a `vault.exe` mutation leaves no record
anywhere except the working tree. Chattiness and off already exist through the
standard `Logging` section; what does not exist is a way to keep the audit trail
without the content, or any trail at all on the second front-end. The cost of
leaving it: logs unsuitable for sharing or long retention while writes flow
through them, and an audit answer of "nothing happened" for CLI mutations.

The fix is an audit-line composer in `VaultShared` - tool name, grant id, bound
arguments, outcome code, with a redaction switch that drops content-carrying
values and keeps identity-shaped ones, driven by the declaration's `Mutates`
flag - consumed by `BearerGrantMiddleware` behind a config flag defaulting to
today's behaviour, and by an opt-in `AuditLogPath` in `vault.settings.json`.
Less obvious than it looks: the e2e log-hygiene probes pin parts of the current
audit format and assert lines are present under the shipped config, so the
shipped defaults must not change; a CLI log write failure must not fail the
tool call it records; and `FileLoggerProvider` creates its timestamped file
unconditionally at startup, so "logging off" still litters `logs/` with empty
files unless creation goes lazy - worth folding in while there.