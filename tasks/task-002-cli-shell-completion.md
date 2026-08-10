# task-002: The CLI offers no shell completion, in PowerShell or anywhere else

**Goal:** Typing `vault ` at a PowerShell prompt and pressing Tab completes command and option names.

## Context

Carried over from `docs/Sidequests.md`, which was retired. The original entry is reproduced in full under Notes; it states what is wrong now, what it costs while it stays, what the fix touches, and what is less obvious than it looks.

## Acceptance criteria

Not yet written - see Open questions.

## Open questions

- How many shells ship? The entry says PowerShell is the one that matters but the directive is shell-neutral, and calls the choice worth making deliberately.
- The entry ends by noting none of this is reachable until `vault.exe` is on `PATH`, which it is not, and that the CLI has no install story. Is the install story a prerequisite task, part of this one, or out of scope with completion landing unreachable?
- A completion invocation should produce no audit or log line, which the entry says should be settled once together with the audit surface work in `task-004`. Which task owns that decision?

## Notes

The entry as it stood in `docs/Sidequests.md`, verbatim:

> ## The CLI offers no shell completion, in PowerShell or anywhere else
>
> Approved: 2026-08-04.
>
> Typing `vault ` at a PowerShell prompt and pressing Tab produces nothing, and nothing about that is accidental: neither half of what completion needs exists. `System.CommandLine` 2.0.10 carries `SuggestDirective`, which answers a `[suggest]` invocation with a newline-delimited candidate list, but it is not part of the default configuration and has to be added to the root command - no file in `src/VaultCli` mentions a directive at all. Above that, PowerShell does not ask an arbitrary executable what its arguments are; it consults a native argument completer registered for that command name in the user's profile, and we ship no such registration. The cost while it stays is borne by whoever drives the CLI by hand: the command tree is the tool vocabulary, which is wide and will get wider, and the only way to discover a tool's name or an option's spelling is `--help` or the source. That is a papercut rather than a defect - nothing computes a wrong answer - but it lands on every interactive use, which is the front-end's whole reason for existing.
>
> The fix is two small pieces that must agree. `ToolCommandTree` or the root-command construction in `VaultCliApp` gains the directive, and a profile script registers `Register-ArgumentCompleter -Native -CommandName vault` pointing back at `vault [suggest]`. Prefer writing that script ourselves over taking `dotnet-suggest`: the package's own documentation names that tool as the consumer of the directive, but it is a separately installed global tool, and the dependency set for this build is closed. What makes it less obvious than it looks: the completer is invoked on every keystroke that triggers it, so a `vault [suggest]` call must return without the work the host does at startup - if configuration loading, vault resolution or the watcher run on that path, completion pauses the prompt, and it must also stay silent rather than emit a failure line into the candidate list when a vault is unavailable. A completion invocation should also produce no audit or log line, which meets the entry above on the audit surface and should be settled once for both. And the registration is per-shell: PowerShell is the one that matters here, but the directive is shell-neutral and bash and zsh shims are the same script written twice, so the choice of how many to ship is worth making deliberately rather than discovering later. Finally, none of this is reachable until `vault.exe` is on `PATH`, which today it is not - the CLI has no install story at all, and completion is the first thing to want one.

The entry's "the entry above on the audit surface" refers to what is now `task-004`.
