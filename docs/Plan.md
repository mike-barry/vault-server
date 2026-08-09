# Plan

The working plan for this build, held by the agent and the operator together.

Read `Brief-Vault-Access-Build.md` first if this session has not. Read `../CLAUDE.md` for the constraints that bind every step.

## How to use it

- Read this at the start of a session and say which step is in play.
- Work one step at a time, in order, unless the operator redirects.
- Check a step off when its work is done and verified, not when it is written.
- Propose changes to this file rather than making them silently.
- This plan does not overrule the specs. A contradiction with them is an escalation, not a plan edit.

## Where the build stands

The software is built and working. The access layer, the tool layer, the MCP server, the CLI, and their tests are all committed on `master`.

Current state, which any step should re-verify rather than trust:

- `dotnet build --no-incremental` - 0 warnings, 0 errors
- `dotnet test` - 1560 passing: 1203 `VaultShared`, 298 `VaultMcpServer`, 59 `VaultCli`

## Done

- [x] **Prework** - spec bundle carried in, vault unreachability verified, retained code swept of stale pointers
- [x] **Design** - four documents under `design/`, reviewed and signed off
- [x] **Build** - core access layer, frontmatter surface, tool infrastructure, all eight tools, both front-ends, and the suite
- [x] **Comment passes** - chronology removed, then rewritten into the MSDN register with `CS1573` holding parameter completeness
- [x] **Obsidian re-measurement** - link resolution measured again and the layer corrected against it
- [x] **Harvest and reconcile** - 944 behavioral cases taken blind from the preserved old repo and checked against this build: 679 matched, 20 findings
- [x] **Triage** - every finding decided with the operator
- [x] **Fix the findings** - all 20 closed, each with the tests that would have caught it
- [x] **Work the coverage list** - fifty candidate gaps triaged against the code and the suite's assertions, twenty-two written, twenty-eight dropped
- [x] **Audit the test suite** - 1267 test methods read against the code they drive, in three phases: sixteen areas reviewed and changed, thirteen fresh readings of those changes, then arbitration from the files. 43 assertions strengthened, 21 duplicates merged, 8 cut, 1 change overturned on review. `conventions/Unit-Tests.md` is the guide that came out of it. Five of the behaviours it found unasserted were then covered, and a Windows-only path case gated
- [x] **Full code review** - `src/` and `tests/` read in five areas, one reviewer each, with a light test pass since the suite audit had just settled it. Ten candidate findings; each was independently attacked by a second reader before it was reported, and five died there. The five that survived became three tasks, all closed with the tests that would have caught them. Eleven suspected code-versus-document disagreements were captured as pointers and quarantined from the conformance audit rather than worked

## What is left

Three steps. The last is the risky one.

They are ordered so that each one's output is settled before the next reads it. The conformance audit follows the review because a review finding can itself be drift from the specs, and the comment sweep runs last so it covers every code change the two made. Reordering them means sweeping comments twice.

### 1. Audit the code against the specs and the design

The last chance to check that the three descriptions of this system agree: what `spec/` requires, what `design/` says was chosen to meet it, and what the code does. After cutover the specs and the design documents are archived and nothing can check them again, so a disagreement left here is permanent and silent - the archived record describes a system that was never built, and no one reading either end can tell.

The check runs both ways, and the second direction is the one that gets skipped. Code that does something no spec requires is one finding. A spec requirement nothing implements, or a design document describing a shape the code no longer has, is the same finding from the other end and is harder to see, because nothing fails and no test is missing. Each design document's `What This Closes` section is a list of decisions with the rule chosen for each; verify those rules still describe the code, since a decision recorded there and quietly changed in the code is exactly the drift this step exists to catch.

Seven specs and four design documents, all under `docs/`. Size the pass so each agent reads a document whole against the code implementing it, rather than sampling across all of them.

`Review-Conformance-Pointers.md` holds eleven places where a code reviewer noticed a document and the code appearing to disagree. **It is not an input to this step and no agent auditing a document may hold it.** A pointer is one reviewer's passing impression - untraced, unrefuted, and as likely to be a misreading as drift - and an auditor reading toward eleven named sites reads away from everywhere else, which loses exactly the drift nobody suspected. That is the drift this step exists to find, since nothing fails and no test is missing. The pointers are opened only after a document's own pass has finished and recorded what it found, and each is then checked and cleared on its own evidence. The file's own header states the procedure and the three outcomes.

Three outcomes for a disagreement, and which one applies is not the agent's call:

- **The code is wrong.** An ordinary change, and it carries its tests.
- **The spec is wrong or stale.** Amend the spec and rewrite the affected design text in the same commit, exactly as any behavior change does.
- **Neither, and the spec cannot settle it.** An escalation. Stop the thread and raise it with the operator; only an agreed escalation is written to `Questions.md`, and the answer returns as an amended spec.

Treat a disagreement as a misread of the specification until the code proves otherwise. This build was written from these documents by agents reading them the same way, so the reading that produced the code is evidence about what the document says.

### 2. Comment sweep, including the test projects' doc references

Run last, so it covers every code change the review and the audit made.

`conventions/Comment-Sweep.md` has the procedure for a sweep over the whole repository against the comment standard. Fold the test projects' dangling doc references into the same pass: the three test projects do not generate documentation, so a broken `<see cref="..."/>` there is invisible. Enable `GenerateDocumentationFile` on each, build, fix what it reports, then revert the project files. That half is mechanical.

### 3. Cut over to vault-server

This build's commits are layered onto the canonical `vault-server` repo rather than replacing its files, so that repo keeps its identity, its remote, and its history.

**Rehearse all of it on a branch in a clone before doing any of it for real.**

Three moves:

1. **Baseline commit** in `vault-server`: delete what this build deleted, align every remaining path to this repo's root commit, and leave untouched any path this repo does not track. `vaults.json` and `.mcp.json` hold the real vault's configuration and no commit here mentions them, so nothing needs restoring afterwards.
2. **Replay** every non-root commit onto that baseline: `git rebase --onto <baseline> <root> master --committer-date-is-author-date`. Start from the root's children - the root's diff is against the empty tree and cannot apply to a non-empty baseline. Author dates are genuine and preserved; matching committer dates to them keeps the graph readable, at the cost that anything keyed on committer date places this work in the past.
3. **Cutover commit**: delete `docs/` entirely, and write the merged `CLAUDE.md` - the canonical repo's as the base, with the comment standard and the `conventions/` pointer grafted in. Most of this repo's `CLAUDE.md` is machinery for `docs/` and loses its referent. `conventions/` carries across as code: it governs the code rather than recording the build.

**The acceptance test is exact.** The resulting tree equals this repo's tree, minus `docs/`, with `CLAUDE.md` replaced, plus the configuration that survived from the canonical repo. Anything else in a diff against this repo means a commit resolved wrongly.

Keep this repo afterwards. It is the only intact record of the build and the thing to diff against if the replay is later found to be wrong.

## Open items

**`BearerGrantMiddleware` logs every call's full arguments.** `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:131` writes `args={Args}` to an unrotated log on every request. With `edit` and `create` shipped, that means note content and frontmatter operations land in `logs/`. This is an operator decision that has not been made, and it is not blocked by anything above.

**The harvest, its reconciliation, and the coverage list are deleted.** Each was the work list for a job that is now finished. Git history keeps them if anyone ever needs to look, and nothing outside it resolves the old case identifiers they carried.

**This does not extend to the specs or the design documents, which stay live until cutover.** They are the specification and the shape chosen to meet it, and no old implementation exists to check them against - which is why a change to behavior still lands its spec amendment and rewrites the affected design text in the same commit, exactly as before. A stale spec is undetectable from inside this repository and leaves the archived record describing something that was never decided. The coverage sweep turned up no defect: every entry it worked was a behavior already correct and merely unasserted, and no test written for it required a change under `src/`. The test-suite audit changed no behavior either, and neither did the five unasserted behaviors covered after it: every test written for them passed against the code as it stood. The code review and the conformance audit are the two steps that can, and the audit exists precisely to find the case where they should have and did not.

**`Questions.md` is empty and unused in this phase.** A gap the specs cannot answer is raised with the operator in session.
