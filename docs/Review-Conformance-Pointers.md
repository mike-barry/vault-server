# Conformance Pointers From The Code Review

Eleven places where a reviewer noticed the code and a document appearing to disagree, captured during
the full code review (step 1) and deliberately not worked there.

**This file is not an input to the conformance audit, and the audit does not read it while auditing.**
Each pointer is one reviewer's passing impression, recorded as a pointer and never as a judgment: none
was traced, none was checked against the document it names, and none was refuted. A pointer that turned
out to be a misreading is as likely here as one that turned out to be drift.

The reason for the separation is that the audit's job is to read a document whole against the code
implementing it and find what disagrees. A list of suspected disagreements handed to it beforehand
changes what it looks at: an auditor holding this file reads toward these eleven sites and away from
everywhere else, and the drift nobody suspected - which is the drift the audit exists to find, because
nothing fails and no test is missing - is exactly what a primed reading misses.

**How this file is consumed.** After the audit has completed its own independent pass over a document
and recorded what it found, the pointers naming that document are opened and checked one at a time.
Three outcomes, the same three the audit's own findings take: the audit already found it, and the
pointer adds nothing; the pointer is real and the audit missed it, which is a finding and also says
something about that pass's coverage; or the pointer is a misreading, and it is struck. Clear each entry
once it is resolved. The file is done when it is empty.

A pointer is not evidence. It survives on what the audit can demonstrate from the document and the code,
exactly as any other finding does.

---

## Access layer, scope, and links

- `src/VaultShared/Access/WikilinkTarget.cs:128` (`Address`) vs `spec/Spec-Obsidian-Link-Behavior.md:Resolution Rule` - the spec says `.` and `..` alone name nothing; the code returns the enclosing folder's path, which `WikilinkUniverse._byPath` can match against a note whose stem is that path.
- `src/VaultShared/Access/VaultFiles.cs:261` (`MoveFile`) vs `spec/Spec-Access-Layer-Capabilities.md:Name Validation` - a file move accepts a destination satisfying the folder grammar alone, so a note can be moved to an extensionless name.
- `src/VaultShared/Access/Wikilink.cs:170` (`WikilinkOccurrence`) vs `spec/Spec-Access-Layer-Capabilities.md:Wikilink Grammar And Resolution` - the spec says each occurrence reports two byte ranges; the extents are UTF-16 character offsets into the decoded text.

## Frontmatter

- `src/VaultShared/Frontmatter/FrontmatterWrite.cs:91` vs `spec/Spec-Frontmatter-Substrate.md:Preservation Invariants` - `request.NewBody` is spliced and composed without `NoteSplit.Normalize`, so entry-point normalization depends on both current callers doing it themselves.

## Tool layer

- `src/VaultShared/Tools/ToolCodes.cs:101` vs `design/Design-Tool-Vocabulary.md:The Error Catalogue` - `write_failed` is in the code's catalogue and not in the document's.
- `src/VaultShared/Tools/ToolExecution.cs:201` vs `design/Design-Tool-Vocabulary.md:The Error Catalogue` - the vault-membership `out_of_scope` carries `{tool, vault}`; the catalogue and `ToolCodes.OutOfScope` both say no payload.

## MCP front-end

- `src/VaultMcpServer/Tools/MintTokenTool.cs`, `src/VaultMcpServer/Tools/ElevationRequestTool.cs` vs `../CLAUDE.md:Layout` - two compiled tools live in the front-end project rather than in `VaultShared`.
- `tests/VaultMcpServer.Tests/FrontEndParityTests.cs` vs `../CLAUDE.md:Layout` - the front-end parity tests are stated to belong to `VaultShared.Tests`.
- `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:127` vs `spec/Spec-Vault-Access-Design.md:Guarantees To Callers` - a named out-of-scope vault is refused as an HTTP 403 plaintext body, so the coded `out_of_scope` envelope is unreachable for that condition.

## CLI front-end

- `src/VaultCli/ToolCommandTree.cs:36` vs `design/Design-Front-End-Projection.md:What This Closes` - the document says `--args` reaches a tool by name without the command tree; it is a per-subcommand option here and cannot name a tool the tree omits.
- `src/VaultCli/CliRendering.cs:102` vs `design/Design-Front-End-Projection.md:Rendering` - the document says `--output json` gives a script one shape whichever tier answered; the content tier is wrapped as `{"content": ...}` while the structured tier is emitted bare.
