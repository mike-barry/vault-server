# Plan

The working plan for this build, seeded from the planning session that produced this bundle. It is this build's own plan, held by the agent and the operator together, and it is expected to change.

## How to use it

- **Read it at the start of a session** and say which step is in play before working. Read `Brief-Vault-Access-Build.md` first if the session has not already.
- **Work one step at a time**, in order, unless the operator redirects. The sequence is deliberate.
- **Check a step off when its work is done and verified**, not when it is written.
- **Keep it current.** Expand a step into sub-steps as it comes into focus, add steps the build discovers, drop steps that turn out to be unnecessary, and re-sequence when the order proves wrong. A step that no longer describes the work is worse than no step.
- **Sweep detail out once it has a home.** A step may carry inputs - a finding, a constraint, something noticed early - while nothing else can hold them yet. Working that step is when they migrate into the design document, the decision, or the code that now owns them, and come out of here. This file holds what was completed and what is still ahead; a detail left behind after its real home exists is a second copy nobody updates.
- **Comments describe the code as it is, never how it got there.** A comment written during the build says what is true now and why, and carries no history of the build itself - no "this used to", "no longer", "moved here from", "a later batch", "as of this step". The reasoning is worth keeping and the chronology is not: it is stale the moment the next step lands, and a reader cannot tell a stale narration from a live constraint. Where a change makes an existing comment describe a world that no longer exists, rewrite it in the same commit rather than leaving it for a sweep. Git history holds the chronology; the code holds the present tense.
- **Propose plan changes rather than making them silently.** The operator is the other holder of this plan; a re-sequencing or a dropped step is a decision they should see.
- **This plan does not overrule the specs.** If working a step surfaces a contradiction with them, that is an escalation under the rules in `../CLAUDE.md`, not a plan edit. The plan says what to do next; the specs say what is correct.

Its neighbours do different jobs: the design documents under `design/` record what was chosen, `Questions.md` carries what the bundle cannot answer, `Sidequests.md` holds agreed work that does not block the build. This file only tracks what is left to do.

## Prework

Runs before any build work begins.

- [x] Carry the spec bundle in - seven specs into `docs/spec/`, the brief in `docs/` - vault frontmatter stripped and cross-references rewritten as file links
- [x] Verify from inside this repo that the knowledge vault is unreachable - no MCP registration, no additional-directory grant, no path out
- [x] Flush out this plan with the operator before any build work begins. What is below is a seed: the phases, their order, what each produces, and where a phase ends are settled together rather than assumed. Bring proposals rather than questions
- [x] Sweep the retained code before design reads it - comments instructing an agent to mirror the deleted PowerShell implementation, citations to documents this repo cannot reach, rationale that is no longer true, and test cases whose subject was the deleted tooling. Rewrite to keep the reasoning and drop the pointer; no behaviour changes, no renaming, no restructuring. Dead code is listed rather than removed, since some of it is spec-named

## Design

Design runs ahead of the code it governs and lands in its own documents under `docs/design/`, split into logical areas rather than accumulated in one. The specs stay the requirement; a design doc says how this build meets it. A design that lands as code is a design nobody reviewed.

The phase opens with two steps that touch code rather than documents. They are groundwork, not design landing early: they relocate code that is already correct and decide nothing the design decides. Their point is that design should reason about the repository as it will be, and that `VaultShared`'s boundaries should be compile errors before anything is designed against them rather than a rule someone remembers.

**A design step is done when its document is written and says what it settles.** The rest of the plan's steps check themselves - the code compiles, the suite is green - and a document has no such test, so review is a step of its own at the end of the phase rather than a gate on each. Bring each document and say what it settles as it lands; the operator reads it then or at the review, and nothing is built on the phase until the review step is done.

- [x] Scaffold the projects - `VaultCli`, `VaultShared.Tests`, `VaultCli.Tests` - so `VaultShared`'s two hard boundaries (no web framework, no `Microsoft.PowerShell.SDK`) are enforced by the compiler from the first file, rather than being a claim about a library nothing consumes
- [x] Migrate the unambiguous half of the retained shared code into `VaultShared` - `VaultScope` and the scope primitives, vault resolution, path policy, the op-class registry, the grant model, and `NativeToolArguments`. Moves, not redesigns; a behaviour change on the way is a separate proposal
- [x] Define the design-document convention, before the first one is written. It lands in `docs/design/README.md`, pointed at from `Brief-Vault-Access-Build.md:Working Files`
- [x] Design the core access layer, resolved-path type first - every later choice depends on whether an out-of-scope path is representable
- [x] Design the frontmatter surface - the block grammar and facet split, the operation vocabulary, outcome-based authority classification, the validator, and the registry's loading and failure behaviour. Ahead of the tool vocabulary, because `edit`'s argument surface is a projection of the operation vocabulary and its advertised schema is generated from the declaration
- [x] Design the tool vocabulary as one surface: argument shapes, return tiers, error vocabulary, the preview convention, the write-class envelope. The largest single task here, and its own phase
- [x] Design the front-end projection - one tool declaration, two front-ends, and what each may vary, and the shared execute path that checks the op-class floor, takes the mutex lock, and constructs the per-call access object
- [x] Operator review of the design, as one pass over the four documents together. The phase's own check: each document was written against its area, and this is where they are read as one design and either signed off or sent back. Nothing in Build starts until it is done

## Build

**The specs and the design documents are both inputs to every step, and are intended to complement each other.** A design document says what shape this build takes where the specs left the shape open. Load both documents whenever touching on an area they cover to get the full picture.

- [x] Build the core access layer
- [x] Build the frontmatter surface - the substrate's single write path and the registry's loader, asserted by the conformance suite against a synthetic declaration
- [x] Build the tool infrastructure - the declaration both front-ends project from, the shared execute path that checks the op-class floor and takes the mutex lock, and the transport-neutral result and error representation
- [x] Build `read`, `glob`, and `grep`
- [x] Build `edit` over the frontmatter surface
  - `BearerGrantMiddleware` logs every call's full arguments verbatim to an unrotated file. `edit` ships, so a write's frontmatter operations and any supplied body now land in `logs/`. The middleware change is an operator decision in flight and is the one input this step did not consume; `create` is the next tool to make it worse
- [x] Verify the read-class scope floor end to end
- [x] Build wikilink maintenance as one shared area - repair on relocation, pinning on creation, the census on removal - over the layer's single grammar and ranking rule. Ahead of the three tools that consume it, so it is not an artifact of whichever was built first
- [x] Build `create`, `move`, and `delete`
- [x] Build the MCP front-end as a projection of the tool declarations
- [x] Build the CLI front-end from the same declarations
- [x] Sweep the code for comments narrating the build rather than describing the code. Every comment should read as a statement about the current design, with the reasoning kept and the chronology dropped. Rewrite in place; do not simply delete, since most of these carry a real constraint wrapped in a stale story
- [x] Audit test coverage against the specs, then commit the suite
- [x] Rewrite the comments into the register of MSDN documentation, over `src/` and `tests/` both. The earlier sweep took the chronology out; this one takes out the voice absorbed from the specs - the bolded theses, the argued justification, the idioms a reader has to have read the bundle to decode - and completes the parameter documentation. Every citation into `docs/spec/` and `docs/design/` goes, and the fact it was carrying is written into the comment, because those documents are archived at cutover and the comment becomes the only copy. The convention lands in `../CLAUDE.md` and `CS1573` holds parameter completeness at build time
- [x] Re-measure Obsidian's link resolution and correct the layer against it. The editor was measured again after the earlier rule was questioned, and it answers in three parts the model had collapsed into one: a target is first read as an address (`/` from the vault root, `./` and `../` from the linking file's folder), a whole vault path then wins outright, and only what remains is ranked. Ranking itself was wrong in two places and matching is looser than whole components. `Spec-Obsidian-Link-Behavior.md` carries the measured rule, in this repository and in the vault's own copy

Tests are built with the functionality they cover and split into files by logical area. Coverage derives from the specs and there is nothing else to derive it from.

## Handoff

What follows the build. The harvest and its reconciliation ran here rather than elsewhere, and the four steps they feed run here too; only the cutover acts on another repository. They are listed together so the build knows where it ends.

- [x] Semantic harvest against the preserved old repo. Ran inside this repo rather than outside it, since the suite was committed and the contamination rule had done its job. 944 behavioral cases in `harvest/`, one file per area, taken blind - no agent doing the harvest had read this repo's specs, designs, or tests
- [x] Reconcile the harvest against the implementation. `Harvest-Reconciliation.md` holds only what was lost: 9 regressions and 11 gaps out of 944 cases, with every deliberate divergence listed in its improvements appendix so the filter can be audited. The per-area reconciliations sit beside their harvests

The four steps below replace the single "reconcile against the committed suite" step this plan used to carry. That step assumed every gap was a case to add and a spec to amend, which was written before anyone knew what the pass would return; some findings are the code disagreeing with a spec that already answers, and some are consequences of a choice nobody wrote down. Triage is what tells them apart, and the specs are the last thing to move rather than the first.

- [ ] Triage the findings in `Harvest-Reconciliation.md`, and drop what is not genuine. Each one's `Recorded` line sorts it: **the spec already requires the old behavior**, so the code is simply wrong and nothing needs amending; **the spec is silent**, so the rule is decided here and recorded where the design is once it lands; or **the spec chose differently and the finding is a consequence to accept**, which is recorded and deleted. Settle the rule for a silent-spec finding at triage rather than after the fix - a specification written from what the code turned out to do is a description, which is the one thing this build was arranged to avoid
- [ ] Fix the genuine findings, each with the tests that would have caught it. Clear the entry from `Harvest-Reconciliation.md` as it lands: the fix is in the code, its test, and the commit message, and the document is a work list rather than a record
- [ ] Sweep the matched-but-untested residue - the suite axis. 679 cases matched the implementation and how many of those the suite asserts is unknown, because each area's reconciliation listed only the unasserted behaviors where containment or safety was at stake. Every area file's `Present in code, unasserted by the suite` section is the seed, not the whole
- [ ] Amend the specs and design documents from what the three steps above settled. This is the only point at which anything in `spec/` changes, and a decision with a code site belongs in a comment there rather than in a spec at all

- [ ] Audit the test projects' doc references before cutover - enable `GenerateDocumentationFile` on the three of them, build, fix what it reports, and revert the project files. They do not generate documentation, so a dangling `cref` there is invisible until someone looks. `conventions/Comment-Sweep.md` has the procedure

- [ ] Cutover: this build's commits are layered onto the canonical `vault-server` repo rather than replacing its files wholesale, so that repo keeps its identity, its remote, and its own history. Three moves, rehearsed on a branch in a clone before any of it is done for real:
  - **Baseline commit** in `vault-server`: delete what this build deleted, align every remaining path to this repo's root commit, and leave untouched any path this repo does not track. `vaults.json` and `.mcp.json` hold the real vault's configuration, no commit here mentions them, so nothing in the replay touches them and nothing needs restoring afterwards
  - **Replay** all 103 non-root commits onto that baseline - `git rebase --onto <baseline> <root> master --committer-date-is-author-date`. Start from the root's children, because the root's diff is against the empty tree and cannot apply to a non-empty baseline. Author dates are genuine and preserved; matching the committer dates to them is what keeps the graph readable, at the cost that anything keyed on committer date places this work in the past
  - **Cutover commit**: remove `docs/` and sweep it into the vault, and write the merged `CLAUDE.md` - the canonical repo's as the base, with the comment standard and the `conventions/` pointer grafted in. Almost everything this repo's `CLAUDE.md` says is machinery for `docs/` and loses its referent here. `conventions/` carries across as code: it governs the code rather than recording the build, and the comment standard is unreachable to a future maintainer if it archives with the specifications it was written alongside
  - The acceptance test is exact: the resulting tree equals this repo's tree, minus `docs/`, with `CLAUDE.md` replaced, plus the configuration that survived from the canonical repo. Anything else in a diff against this repo means a commit resolved wrongly. Keep this repo afterwards - it is the only intact record of the build, and the thing to diff against if the replay is later found to be wrong
