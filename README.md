# vault-server

A localhost [Model Context Protocol](https://modelcontextprotocol.io) server and companion CLI, in C# on .NET 10, that give AI agents scoped, audited access to my markdown knowledge base.

> **Status:** This is a personal project in continuous development, not a finished product. It is built for my own vaults on my own machines, its tool surface and configuration change as the design settles, and some planned tools are not built yet. It is public so the work can be seen, not as a supported tool.

## What It Is

My notes live in an [Obsidian](https://obsidian.md) vault: a git repo of markdown files that AI agents read and maintain alongside me. Giving an agent raw filesystem access to that vault means trusting every agent, including unattended ones, with all of it. This server puts one access layer between agents and the vault, so what an agent can see and change is decided by the token it holds rather than by what it chooses to do.

- **One access layer owns all vault I/O.** Path containment and scope are enforced by construction: every operation resolves through the layer, so no tool can reach outside the vault or outside the folders its caller is scoped to.
- **Tools are transport-neutral.** Each tool is declared once in a shared library and projected into two front-ends, the MCP server and the `vault` CLI, which render the same results and errors in their own way. Parity tests hold the two to the same semantics.
- **Access is granted, not assumed.** Bearer tokens map to grants that name the operation classes allowed (`content:read`, `content:write`, `git:commit`, and others), the vaults reachable, the folders in scope, and a lifetime. An agent can mint a narrower, time-limited token for a subagent, and can ask for elevation, which fails closed unless an approver is configured.
- **Every call is audited.** Each request logs the grant id, never the token, along with the tool and its arguments.
- **Frontmatter is schema-checked.** A registry declares the YAML frontmatter keys a vault uses, and the layer validates writes against it.

## Tools

| Tool | Purpose |
|---|---|
| `read` | Read a note whole, or just its frontmatter or body |
| `glob`, `grep` | Enumerate and search vault content, backed by ripgrep |
| `create`, `edit` | Write vault content within the caller's scope |
| `move` | Rename or relocate a note or folder, rewriting the Obsidian wiki-links that point into it |
| `delete` | Remove a note or folder, reporting any wiki-links it leaves orphaned |
| `health_check` | Report the server's configuration and load state |
| `mint_token`, `request_elevation` | Issue scoped tokens and request temporary added authority (MCP only) |

The MCP server can also load PowerShell script tools at runtime; that framework is in place with no scripts shipped. `sync` and `lint` tools are planned and not built.

## Layout

```
src/VaultShared/      - the access layer and tool layer; no web or PowerShell dependencies
src/VaultMcpServer/   - the MCP front-end: hosting, HTTP auth, tool registration, script loading
src/VaultCli/         - the CLI front-end, compiled to vault.exe
src/VaultWatcher/     - placeholder for a planned application
tests/                - xUnit suites for the shared layer and each front-end
test-vault/           - a small synthetic vault the tests run against
docs/                 - conventions, guides, and archived design specs
```

## Building and Running

Requires Windows, the .NET 10 SDK, and [ripgrep](https://github.com/BurntSushi/ripgrep) on `PATH`.

```
dotnet build
dotnet test
```

The server reads `vaults.json` (which vaults exist and where; start from `vaults.example.json`), `op-classes.json`, and `grants.json` (tokens and their grants; start from `grants.example.json`). `run.ps1` starts it on `http://localhost:5111/mcp`, bound to loopback only. `publish.ps1` builds `vault.exe` into `bin/vault/`.

## Related

- [vault-backend](https://github.com/mike-barry/vault-backend) - the Claude Code skills, guides, and templates that operate on the same vaults

## About

Built and maintained by Mike Barry as a personal project.
