# task-010: Audit the code against the specs and the design

**Goal:** Every disagreement between what `docs/spec/` requires, what `docs/design/` says was chosen to meet it, and what the code does has been found and resolved, in whichever of the three was wrong.

## Context

This is the last chance to check that the three descriptions of this system agree. At cutover ([task-012](task-012-cut-over-to-vault-server.md)) the specification and design stop travelling with the code, because they are not to be maintained: the implementation goes on changing elsewhere and nothing checks them against it again. A disagreement left here is therefore permanent and silent — the record describes a system that was never built, and nobody reading either end can check.

**What becomes of that record is undecided** ([task-012](task-012-cut-over-to-vault-server.md), Notes), and one possible answer is that it is not kept. That does not shrink this task, and it changes only one thing about it. Most of what this pass finds is a fact about the code — code doing something nothing asked for, a decision recorded in `What This Closes` and quietly changed since — and those findings are worth having whether or not a document survives to record them. The part that does depend on the answer is the migration: facts established by measurement, which live in the specification and nowhere else, have to reach the code sites that depend on them, and that becomes the whole point of the pass rather than a side effect of it if the documents are eventually dropped. Re-read this before starting, and check where that decision landed.

**The check runs both ways, and the second direction is the one that gets skipped.** Code that does something no spec requires is one finding. A spec requirement nothing implements, or a design document describing a shape the code no longer has, is the same finding from the other end and is harder to see, because nothing fails and no test is missing. Each design document's `What This Closes` section lists decisions with the rule chosen for each; verify those rules still describe the code, since a decision recorded there and quietly changed in the code is exactly the drift this exists to catch.

Seven specs and four design documents. Size the pass so each agent reads a document whole against the code implementing it, rather than sampling across all of them.

Two standing constraints bind this work. **This repo has no access to the knowledge vault and must not seek one** — the as-built documentation of the replaced system is deliberately out of reach, and a judgment grounded in it reintroduces the shape this build exists to avoid. And **much of the code carried forward from the previous implementation and is already correct**: auth and authorization, the scope primitives, health, logging, configuration loading, host wiring, and the script-loading framework. Where a spec covers such a component's subject, the specs win and the finding is a proposed change to the code. Where no spec covers it, a component that looks wrong is more likely a misread requirement — raise it.

### The quarantined pointers

`docs/Review-Conformance-Pointers.md` holds eleven places where the code review noticed a document and the code appearing to disagree. **It is not an input to this task and no agent auditing a document may hold it.** A pointer is one reviewer's passing impression — untraced, unrefuted, and as likely to be a misreading as drift — and an auditor reading toward eleven named sites reads away from everywhere else, which loses exactly the drift nobody suspected. That is the drift this task exists to find.

The pointers are opened only after a document's own pass has finished and recorded what it found, then checked and cleared one at a time on their own evidence. The file's own header states the procedure and the three outcomes. It is done when it is empty, and is deleted then.

### Build-Decisions.md

`docs/Build-Decisions.md` records in-flight judgment calls made during the build, where the specs and design left a small gap and the obvious solution was taken rather than stopping. **When a discrepancy turns up and the reason for it is not apparent, it is a place to look for why.** An entry may name the reasoning that produced the code.

**Treat it as old and potentially stale.** It was written as work happened and was never swept: most of it predates later changes, an entry may describe a decision since overturned, and nothing has verified any of it against the current code. It is a lead, never evidence. A finding stands or falls on the document and the code, and an entry that explains a discrepancy still leaves the discrepancy to resolve under the three outcomes below.

**It is deleted when this task concludes**, whether or not it was consulted. Anything in it worth keeping migrates first — to a design document's `What This Closes`, or to a comment at the code site.

### The three outcomes

Which one applies is not the auditing agent's call:

- **The code is wrong.** An ordinary change, and it carries its tests.
- **The spec is wrong or stale.** Amend the spec and rewrite the affected design text in the same commit, exactly as any behavior change does.
- **Neither, and the spec cannot settle it.** An escalation. Stop the thread and raise it with the operator in session; the answer returns as an amended spec.

Treat a disagreement as a misread of the specification until the code proves otherwise. This build was written from these documents by agents reading them the same way, so the reading that produced the code is evidence about what the document says.

## Acceptance criteria

- [ ] Each of the seven specs and four design documents has had a pass reading it whole against the code implementing it, and what that pass found is recorded.
- [ ] The reverse direction is covered for each: a requirement nothing implements, and a design document describing a shape the code no longer has.
- [ ] Every `What This Closes` rule in the four design documents has been verified to still describe the code.
- [ ] Every disagreement is resolved under one of the three outcomes, and each behavior change landed its spec amendment and design rewrite in the same commit.
- [ ] `docs/Review-Conformance-Pointers.md` is empty and deleted, each pointer having been checked after its document's own pass and either confirmed as a finding or struck as a misreading.
- [ ] `docs/Build-Decisions.md` is deleted, with anything worth keeping migrated to a design document or a code comment first.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

Run [task-009](task-009-windows-only.md) first. If the specs state Windows while the code still branches on platform, this audit raises a finding per branch and the same decision is triaged many times over.
