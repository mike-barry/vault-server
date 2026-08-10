# task-010: Audit the code against the specs and the design

**Goal:** The specification and the design describe the code as it actually is, and every place they do not has either been corrected in the document or raised as a question about the code.

## Context

This is the last chance to check that the three descriptions of this system agree. At cutover (`task-012-cut-over-to-vault-server.md`) the specification and design stop travelling with the code, because they are not to be maintained: the implementation goes on changing elsewhere and nothing checks them against it again. A disagreement left here is therefore permanent and silent — the record describes a system that was never built, and nobody reading either end can check.

**The documents are kept.** How they are frozen and marked as archived is still to be settled and is not decided here (`task-012-cut-over-to-vault-server.md`, Notes); this task is what makes them worth freezing. Everything below assumes they survive as the originating record of the build, accurate as of the commit this task ends on.

**The code is the source of truth from here on.** The specification and the design were the authority while the system was being built; they are now a description of a system that exists, and where the two disagree the document is the thing that moves. That inverts the usual direction of a conformance pass and is the single most important thing about this one.

**The check runs both ways, and the second direction is the one that gets skipped.** Code that does something no spec requires is one finding. A spec requirement nothing implements, or a design document describing a shape the code no longer has, is the same finding from the other end and is harder to see, because nothing fails and no test is missing. Each design document's `What This Closes` section lists decisions with the rule chosen for each; verify those rules still describe the code, since a decision recorded there and quietly changed in the code is exactly the drift this exists to catch.

Seven specs and four design documents. Size the pass so each agent reads a document whole against the code implementing it, rather than sampling across all of them.

Two standing constraints bind this work. **This repo has no access to the knowledge vault and must not seek one** — the as-built documentation of the replaced system is deliberately out of reach, and a judgment grounded in it reintroduces the shape this build exists to avoid. And **much of the code carried forward from the previous implementation and is already correct**: auth and authorization, the scope primitives, health, logging, configuration loading, host wiring, and the script-loading framework. A carried-forward component that looks wrong against a document is far more likely a misread requirement than a defect.

### Writing the documents

Amendments are written to the prose standard in `docs/conventions/Code-Comments.md` — plain sentences, active voice, concise, self-contained, no thesis paragraphs, present tense with no chronology. The rules there that are about C# specifically, such as `<param>` completeness and what counts as an inline comment, do not apply. `docs/design/README.md` remains the convention for how a design document is structured.

Amended text describes the system as it is. It does not say what changed, when, or that it was corrected by this audit — the commit carries that.

A `What This Closes` entry is a statement of the rule in force, not a record of how it was reached. An entry the code no longer supports is rewritten to state what the code now does, or dropped if the point it closed is no longer open. Nothing there argues a change, notes that an earlier rule was superseded, or keeps a rejection clause for an option the code has since taken.

### The quarantined pointers

`docs/Review-Conformance-Pointers.md` holds eleven places where the code review noticed a document and the code appearing to disagree. **It is not an input to this task and no agent auditing a document may hold it.** A pointer is one reviewer's passing impression — untraced, unrefuted, and as likely to be a misreading as drift — and an auditor reading toward eleven named sites reads away from everywhere else, which loses exactly the drift nobody suspected. That is the drift this task exists to find.

The pointers are opened only after a document's own pass has finished and recorded what it found, then checked and cleared one at a time on their own evidence. The file's own header states the procedure. Its own outcomes are about the pointer — the audit already found it, the audit missed it, or it is a misreading — and a pointer that survives is then resolved under the two outcomes below like any other finding. It is done when it is empty, and is deleted then.

### Build-Decisions.md

`docs/Build-Decisions.md` records in-flight judgment calls made during the build, where the specs and design left a small gap and the obvious solution was taken rather than stopping. **When a discrepancy turns up and the reason for it is not apparent, it is a place to look for why.** An entry may name the reasoning that produced the code.

**Treat it as old and potentially stale.** It was written as work happened and was never swept: most of it predates later changes, an entry may describe a decision since overturned, and nothing has verified any of it against the current code. It is a lead, never evidence. A finding stands or falls on the document and the code, and an entry that explains a discrepancy still leaves the discrepancy to resolve under the two outcomes below.

**It is deleted when this task concludes**, whether or not it was consulted. Anything in it worth keeping migrates first — to a design document's `What This Closes`, or to a comment at the code site.

### The two outcomes

- **The document is stale.** The ordinary case, and the reason this pass exists. Amend the spec and rewrite the affected design text so both describe what the code does. No code changes.
- **The code looks wrong.** Not fixed here. Record it in `docs/Audit-Code-Findings.md` and carry on with the pass; the operator reviews the file once the audit is done and decides what happens to each entry.

**No code changes land under this task.** A finding about the code is written down, not acted on, however clear the fix looks. That keeps the audit a single reviewable judgment about the code rather than a pass that quietly edits behavior while claiming to edit documents.

The bar for an entry is substance and consequence: a caller could get a wrong answer, lose data, be refused something it is entitled to, or the code contradicts a decision recorded in a `What This Closes` section. Wording, naming, a message that reads oddly, or a document being merely less precise than the code are not entries — those are amendments to the document, or nothing at all. A file of nitpicks buries the two findings worth reading.

Each entry is short and self-contained: what the document requires, what the code does, `file.cs:line`, why it matters, and the recommended fix — or the options with a recommendation where there is a real choice. The file is deleted once the operator has closed out every entry.

Treat a disagreement as a misread of the specification until the code proves otherwise. This build was written from these documents by agents reading them the same way, so the reading that produced the code is evidence about what the document says.

## Acceptance criteria

- [x] Each of the seven specs and four design documents has had a pass reading it whole against the code implementing it, and what that pass found is recorded.
- [x] The reverse direction is covered for each: a requirement nothing implements, and a design document describing a shape the code no longer has.
- [x] Every `What This Closes` rule in the four design documents has been verified to still describe the code.
- [x] Every disagreement is resolved under one of the two outcomes: the document amended to match the code, or an entry in `docs/Audit-Code-Findings.md`.
- [x] The specification and design describe the code as it is, and no amendment introduced a claim the code does not support.
- [x] `docs/Review-Conformance-Pointers.md` is empty and deleted, each pointer having been checked after its document's own pass and either resolved under one of the two outcomes or struck as a misreading.
- [x] `docs/Build-Decisions.md` is deleted, with anything worth keeping migrated to a design document or a code comment first. Two facts survived the triage and are held in `docs/Audit-Code-Findings.md` for review before they are written; everything else was already recorded at a code site or in a design document, and four entries were contradicted outright by the current code.
- [ ] `docs/Audit-Code-Findings.md` has been reviewed with the operator. It is deleted once every entry is closed out; entries the operator wants acted on become their own tasks.
- [x] `dotnet build --no-incremental` clean, `dotnet test` green. 0 warnings, 0 errors, 1570 tests passing - ten more than before this task, from the four code changes the findings review settled. `e2e-smoke.ps1` was updated for the removed transport gate and has not been run; it needs a live server.

## Notes

`task-009-windows-only.md` is done, so the specs and the code agree that Windows is the platform. A leftover platform branch is a code finding, not a document one.

**The independent pass over each document is complete.** One agent per document read it whole against the code, and every candidate finding went to a second agent instructed to refute it. 81 candidates: 37 confirmed, 9 confirmed with a corrected description, 35 struck as misreadings. The struck ones are worth knowing about as a group - most were one sentence read without the paragraph that qualifies it, or a rule about the editor's behaviour read as a rule about every code path. The full per-document reports, including candidates each pass rejected before reporting, are the `audit-*.md` files in this session's scratchpad.

Six findings are held for the operator in `docs/Audit-Code-Findings.md` rather than amended: the documents they name are deliberately left untouched, so amending them later is part of closing each entry out.

**The eleven quarantined pointers are resolved and the file is deleted.** Six the audit had already found on its own, which is the useful measure of its coverage: the `.`/`..` resolution, the wikilink extents, `write_failed`, the `out_of_scope` payload, `--args`, and the `--output json` shapes. Two became findings for the operator - `.`/`..` and the HTTP 403 that pre-empts the coded refusal. Three were struck: the move destination grammar is broader at the layer than the spec's rule but unreachable through the only caller, which holds destinations to the operand's own kind; `NewBody` is normalized by both callers before it reaches the write path; and the front-end parity tests are where `CLAUDE.md` says, with the shared contract in `VaultShared.Tests` and each transport suite asserting its own half.

One pointer resolved into a `CLAUDE.md` gap rather than a code or spec one: `MintTokenTool` and `ElevationRequestTool` are compiled tools living in `VaultMcpServer`, which the Layout section does not mention. They belong there - both need a client session the CLI has no concept of, so the CLI would not duplicate them - but the section reads as though the front-end projects carry no tools. `CLAUDE.md` is reconciled in `task-012-cut-over-to-vault-server.md` and was left alone here.
