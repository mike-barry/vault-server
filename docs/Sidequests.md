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

## The CLI offers no shell completion, in PowerShell or anywhere else

Approved: 2026-08-04.

Typing `vault ` at a PowerShell prompt and pressing Tab produces nothing, and nothing about that is accidental: neither half of what completion needs exists. `System.CommandLine` 2.0.10 carries `SuggestDirective`, which answers a `[suggest]` invocation with a newline-delimited candidate list, but it is not part of the default configuration and has to be added to the root command - no file in `src/VaultCli` mentions a directive at all. Above that, PowerShell does not ask an arbitrary executable what its arguments are; it consults a native argument completer registered for that command name in the user's profile, and we ship no such registration. The cost while it stays is borne by whoever drives the CLI by hand: the command tree is the tool vocabulary, which is wide and will get wider, and the only way to discover a tool's name or an option's spelling is `--help` or the source. That is a papercut rather than a defect - nothing computes a wrong answer - but it lands on every interactive use, which is the front-end's whole reason for existing.

The fix is two small pieces that must agree. `ToolCommandTree` or the root-command construction in `VaultCliApp` gains the directive, and a profile script registers `Register-ArgumentCompleter -Native -CommandName vault` pointing back at `vault [suggest]`. Prefer writing that script ourselves over taking `dotnet-suggest`: the package's own documentation names that tool as the consumer of the directive, but it is a separately installed global tool, and the dependency set for this build is closed. What makes it less obvious than it looks: the completer is invoked on every keystroke that triggers it, so a `vault [suggest]` call must return without the work the host does at startup - if configuration loading, vault resolution or the watcher run on that path, completion pauses the prompt, and it must also stay silent rather than emit a failure line into the candidate list when a vault is unavailable. A completion invocation should also produce no audit or log line, which meets the entry above on the audit surface and should be settled once for both. And the registration is per-shell: PowerShell is the one that matters here, but the directive is shell-neutral and bash and zsh shims are the same script written twice, so the choice of how many to ship is worth making deliberately rather than discovering later. Finally, none of this is reachable until `vault.exe` is on `PATH`, which today it is not - the CLI has no install story at all, and completion is the first thing to want one.

## A file whose name ends in the note extension can take a link belonging to another

Approved: 2026-08-04.

`WikilinkUniverse` indexes every note twice in `_byPath`, under its whole vault path and under that path with the note extension removed, so that a target may omit `.md` the way the editor writes it. Both writes are plain assignments, so two notes can claim one key and whichever the enumeration reaches last wins. A vault holding both `Notes/Report.md` and `Notes/Report.md.md` is the case: the second registers the key `Notes/Report.md` as its stem, the first registers the same key as its whole path, and a link written `[[Notes/Report.md]]` opens whichever of the two happened to enumerate later. Enumeration order is the search engine's, so the answer is stable in practice but is not decided by anything meaningful, and the same link can open a different note on another machine.

The cost of leaving it is small and bounded. It needs a filename ending in a doubled extension, which nothing in the vault creates deliberately, and the failure is a link opening the wrong note rather than any data loss. The exact-path tier is the tier that cannot be overruled, though, so when it does go wrong nothing downstream can correct it: relocation repairs the link to the wrong destination and the removal census reports the wrong orphan, both silently.

What the fix touches is `WikilinkUniverse`'s constructor and, unavoidably, a decision about which note should win. Obsidian was not measured on this pair, so the ordering is not known - the editor's own answer should be measured before the code picks one, the same way the rest of the resolution rule was established. The likely rule is that a whole-path registration beats a stem registration, since the whole path is the more literal reading of the target text, which would mean registering stems in a second pass and never letting one overwrite a path. What makes it less obvious than it looks: `_byName` has the same shape and does not have the same problem, because it holds a list per key rather than a single position, so the fix is not simply copying what `_byName` does; and the two dictionaries must stay consistent about which note a name reaches, or the exact-path tier and the ranked tier will disagree about the same file.

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