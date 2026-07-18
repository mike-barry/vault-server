# CLAUDE.md - Vault MCP Server

## Vault Corpus Reference

This project's authoritative design/structure docs live in the vault repo's corpus, not here:

- `C:\Repos\vault-primary\Projects\Vault-MCP-Server\Corpus\Server-Structure.md` - repo layout, host composition, chokepoint, auth/grants, tool dispatch
- `C:\Repos\vault-primary\Projects\Vault-MCP-Server\Corpus\Testing.md` - test suites, shared case files, fixture conventions
- `C:\Repos\vault-primary\Projects\Vault-MCP-Server\Corpus\Dev-Setup.md` - dev environment prereqs
- `C:\Repos\vault-primary\Projects\Vault-MCP-Server\Inbox\Plan-Vault-MCP-Server-Implement.md` - implementation log/checklist (historical, not authoritative)

A dev session rooted in this repo has read access to `C:\Repos\vault-primary` via the `permissions.additionalDirectories` grant in `.claude/settings.json`.

**Read the relevant corpus doc above before making any structural change to this repo** - it reflects current state; don't re-derive or guess at design decisions already documented there.
