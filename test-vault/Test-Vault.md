# Test-Vault.md - Fixture Vault for VaultMcpServer

A small standalone vault used by [[VaultMcpServer]] tests to exercise multi-vault behavior without touching the real vault. Mirrors the real vault's PARA-lite layout and document conventions at miniature scale.

## Structure

```
test-vault/
  Projects/
    Widget/        - sample project with folder note, tasks, and a planning doc
  Resources/       - reference notes that other notes link to
  Inbox/           - loose notes
```
