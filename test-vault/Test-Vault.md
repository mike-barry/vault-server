# Test-Vault.md - Fixture Vault

A small standalone vault the test suite runs against, so tests can exercise multi-vault behaviour, path handling and link handling without needing a vault that belongs to anyone. Every note in it is fabricated. Nothing here is drawn from a real vault, and nothing added later should be.

The layout is a miniature PARA-lite tree, which is enough shape for scope roots to be interesting - nested folders, a folder note, and notes that link across folders.

[[Missing-Note]] does not resolve, and is meant not to. Link handling needs a dangling target to work against, so this one is kept deliberately rather than fixed. It is the only intentional one: treat any other unresolved link as a defect in the fixture.

## Structure

```
test-vault/
  Projects/
    Widget/        - sample project with folder note, tasks, and a planning doc
  Resources/       - reference notes that other notes link to
  Inbox/           - loose notes
```
