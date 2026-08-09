# CLAUDE.md - Vault Access Layer

One access layer owning all vault I/O, with containment and scope enforced by construction; a tool layer above it with no hosting dependencies and a transport-neutral result and error representation; and two front-ends over that tool layer - an MCP server and a CLI - which render that representation differently and are otherwise indistinguishable to a tool.

## Layout

**Everything a tool call needs is in `VaultShared`; a front-end project holds only what makes its transport work.** The test is whether the CLI would have to duplicate it.

- **`VaultShared`** - the access layer and the tool layer. A plain `Microsoft.NET.Sdk` library.
- **`VaultMcpServer`** - the MCP front-end: host wiring, HTTP auth, MCP registration and schema projection, and the script-loading framework.
- **`VaultCli`** - the CLI front-end, compiled to `vault.exe`.
- **`VaultWatcher`** - a stub, and the third consumer of shared. It derives the mutex lock's identity from the same code rather than a re-implemented convention, which is why that derivation lives in shared, reachable by a consumer that is not a front-end.

Two references `VaultShared` must never take:

- **No web framework.** Its SDK choice makes hosting types unavailable, which turns "no hosting dependencies in the layer or the tools" from a rule someone must remember into a compile error.
- **No `Microsoft.PowerShell.SDK`.** Measured: adding that reference to a console app takes its framework-dependent publish output from 175 KB across 5 files to 67 MB across 433, whether or not a single PowerShell type is ever touched. Startup is unaffected - the cost is entirely distribution size. The CLI runs no scripts and must not carry the runspace host, which is also why script tools are MCP-only.

Three test projects. `VaultShared.Tests` is the conformance suite, asserts the substrate against a synthetic declaration, must not need a web host to run, and owns the front-end parity tests - that one tool declaration projects into both front-ends with the same semantics is a claim about both, so it belongs to neither front-end's own suite. `VaultMcpServer.Tests` and `VaultCli.Tests` cover their own transports; the CLI's most important case is the fail-closed path, since an untested one quietly becomes fail-open.

## Build and test

```
dotnet build --no-incremental
dotnet test
```

Both are expected clean: 0 warnings, 0 errors, and 1560 tests passing.

## Constraints

- **The external dependencies are chosen.** Ripgrep for search and enumeration, YamlDotNet for frontmatter, `System.CommandLine` for the CLI's command tree, and CliWrap for invoking ripgrep. Do not evaluate alternatives to any of them.
- **The empty `tools/` folder is intentional.** The script-loading framework is a capability with no scripts to load. The folder and its `.gitkeep` stay: the watcher only attaches to a directory that exists, and it decides that once at construction.
- **Junctions, symbolic links, and reparse points are not accommodated.** The layer assumes a plain directory tree and neither detects nor handles them.
- **`lint`, `lint_suppressions`, and `sync` are not built**, and the tool surface is not designed around them.

## Specifications

`docs/spec/` states what this system must do and `docs/design/` states the shape chosen to meet it; `docs/design/README.md` is the convention the design documents follow. A change to behavior amends the spec and rewrites the affected design text in the same commit - nothing else here states what the system is required to do, and a stale spec is undetectable from inside this repository.

A decision the specs left open lands in a design document's `What This Closes` section, and a decision with a code site is a comment there, written as a constraint rather than a story. There is no separate decision log: a central one is read by nobody and duplicates what the design document and the commit message already say between them.

On a question the specs cannot answer, stop that thread and raise it with the operator in session rather than deciding from judgment. The answer returns as an amended spec, which is the requirement.

## Comments and tests

**Comments are written in the register of MSDN documentation, and `conventions/Code-Comments.md` is the standard.** Read it before writing or modifying a comment; `conventions/Comment-Sweep.md` covers running a sweep over the whole repository. Three rules hold whether or not it has been read. A summary opens with a verb phrase and says plainly what the thing does. A documented member documents every one of its parameters - `CS1573` enforces that at build time on the three `src/` projects. And a comment stands on its own: it references no document, leans on no compressed idiom and no fact stated elsewhere, so a reader who has opened nothing but the file in front of them understands it on first read. A fact worth keeping is stated in the comment itself, including what it was measured or agreed against and what changing it breaks.

**`conventions/Unit-Tests.md` is the standard for the tests.** A test asserts a behaviour a caller can observe. Read it before adding or changing one.

## Tasks

`tasks/tasks.md` is the task index, and `tasks/tasks-instructions.md` says how to read and maintain it. **Read the instructions before changing the index or any `task-*.md` file** - they carry the section meanings, the line format, the ID rules, and the archiving flow, and none of that is inferable from the index alone.

Two rules hold whether or not that document has been read. **Only the operator adds a task**, in either form - a line in the index or a task file. An agent that finds something worth doing describes it and waits. And **only the operator archives one**: judging the work finished is not the same as the operator agreeing it is, so say the task looks complete and leave it in place until told.
