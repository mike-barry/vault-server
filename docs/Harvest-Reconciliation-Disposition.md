# Harvest Reconciliation — Disposition

Triage of `Harvest-Reconciliation.md` is **complete**: every finding, observation and untested
containment property in it has been worked and decided with the operator. This file records those
decisions. What follows triage is the fix step, then the suite sweep - `Plan.md` carries both.

**An entry is deleted from this file and from the parent document together, in the commit that makes
it true.** A group of coupled changes lands its code, its tests, the design document's text and the
spec amendment where it has one, and clears both documents' entries for what it closed. Both files
are empty when the work is done: they carry context until it is written somewhere permanent, and
nothing in them is a record. Triage decided things and changed no code, so nothing was cleared then.

## What an entry is, and what it is not

An entry is an instruction to the fix step, written in the voice of a change: it says what the code
does now, what it should do instead, and why that was decided. That voice belongs here and nowhere
else.

**What lands in a comment or a design document is the present tense only.** A comment says what is
true now (`conventions/Code-Comments.md`, "Present tense, no chronology"), and a design document
describes the design as it is, rewritten wholesale where it changes, with no account of how it got
there (`design/README.md`). So an entry saying a rule was reversed, restored, or corrected is telling
the fix step what to change - not supplying words to transcribe. What the destination gets is the
rule: what holds, what it was measured or agreed against, and what changing it breaks.

The history stays in this file, which archives with the rest of `docs/` at cutover, and in the commit
that lands each change.

## Reading an entry

Each entry says what was decided and why. It does not restate the finding, so read the parent's entry
alongside it.

`R#` and `G#` are the parent document's own headings and grep directly:

```bash
grep -n -A20 '^### R2 ' docs/Harvest-Reconciliation.md
```

`O#` and `C#` are assigned here. The parent's `Observations` and `Containment properties with no
test` sections are unlabelled bullets, numbered here in the order they appear there. Each entry below
carries a grep matching its bullet's own text - for example:

```bash
grep -n -A4 'argument binder accepts unknown members' docs/Harvest-Reconciliation.md   # O1
```

A finding's own case IDs resolve into `harvest/` by their three-letter prefix; the parent document's
"Looking something up" section has those commands.

## The fix batch

What triage produced, for whoever works the fix step. Every item is detailed in its entry below.

Code changes, with their couplings:

- **`MoveTool` plan-phase refusals** - R2 and R6 are the same few lines and land together
- **Mapping field emptiness** - R3
- **Search engine unavailable: its own code, plus a startup and health check** - R7
- **Folder-create occupancy probe** - R8
- **Post-move paths in `MoveResult`** - R9
- **`unknown_key` payload** - G5
- **The vault named in the inherited-lock message** - G6
- **Leading-separator trim** - G8
- **The inline case-insensitive flag named in `grep`'s pattern description** - G9
- **Attachment embeds repaired through the existing pass** - G10
- **`expected_content_hash` accepted on any edit** - G11, and **O1** (`UnmappedMemberHandling.Disallow`)
  lands with it rather than after, for the reason O1 gives
- **`WikilinkRanking.Compare` rewritten to the re-measured rule** - O3
- **`Grant.Allows` owning its comparison** - C4
- **Three comment rewrites** - O4, O5, and the whitespace class named in G3

Decisions to record in the design documents: R1, R4 with R5, G1 with G2, G3, G4, G7, O2.

Four spec amendments, each landing with the group that makes it true: the Markdig arbiter line (G3),
the check-and-write rule that cannot hold for directory creation (R8), the extensionless-target rule
measured 2026-08-08 (G10), and attachment emit form written in as an open measurement (G10).

Carried to the suite sweep: C1-C3, which are test gaps rather than code defects and are that step's
highest-value entries.

One correction to the parent document's own appendix: its `Improvements Taken` entry for LNK-025
records the link-ranking reversal as deliberate and spec-backed, which O3 establishes was measured on
the wrong surface. The appendix is not edited; a reader should not take that entry at face value.

## Dispositions

### R1 — accepted, no change

`delete` cannot require the elevated class for a note identified by its *keys* rather than its
location. Ruled not an issue: the two agentic classes that matter are already covered. Guides under
a dot-directory are unreachable by every tool, and a folder note is a declarable shape
(`PathPolicy`'s `folder_note` structural rule), which `delete` does consult. The shipped
`vaults.json` declares no `mutation_requires`, which is a configuration choice rather than a code
defect.

### R2 — genuine, to fix

The spec already requires the old behavior (`Spec-Tool-Capabilities.md:151`, `:280`), so nothing is
amended and the code moves. Decide destination occupancy, missing parent and destination-name
grammar in `MoveTool`'s plan phase, ahead of `WikilinkMaintenance.Apply`, with the refusal mapping in
`MoveTool.Refused` moving with them.

### R3 — genuine, to fix

The spec already requires the old behavior: `Spec-Frontmatter-Substrate.md:113` holds that emptiness
is tested one way across an operation's additive and subtractive forms. Test `OperationText.IsEmpty`
on a mapping field's value in `FrontmatterWrite.ValidatePayload`, and fail the validator's
required-field check on empty text as well as absent text. Neither site has a test today.

### R4 — accepted, record the choice

The spec is silent, and partial failure is a designed, reported state
(`Spec-Tool-Capabilities.md:282`): a failure names what landed and what did not and carries ordered
recovery steps, which a `move` does. A writability pre-flight would narrow the window rather than
close it - a note can be read-only, locked by another process, or on a full disk, and only the first
is cheaply probeable - so the partial path stays either way. Record the rule in
`Design-Tool-Vocabulary.md`'s `What This Closes`: no mutating pass pre-flights its write targets.

### R5 — accepted, record with R4

The same trade as R4, and settled the same way. A removal is the costlier case, since what a
half-finished delete removed is gone where a half-finished move re-runs idempotently, but a
pre-flight would not close that window either. Record it in the same rule.

### R6 — genuine, to fix with R2

The same root as R2 and the same spec line (`Spec-Tool-Capabilities.md:280`): a refusal decidable
from the arguments alone is being discovered by the apply path. `MoveTool` compares source and
destination spellings for equality only, so a folder moved beneath itself is planned, the repair is
written, and `Directory.Move` then fails - leaving a rewritten link graph and printing a recovery
step ("re-run the identical move") that can never succeed. Refuse a destination at or beneath the
source in the plan phase, with the R2 checks.

### R7 — genuine, to fix in both halves

The classification half is spec-required: `Spec-Tool-Capabilities.md:163` holds that a permanent
refusal must not route into a retrying branch, and `:162` names search as the pair most easily
collapsed. A missing engine binary reports `search_failed`, documented retryable, so an automated
caller retries a call that can never succeed. `SearchEngine` already separates a failed launch from a
failed run (`LaunchFailed`), so the distinction exists and is discarded above it: give the engine
being unavailable its own code. Error codes are not part of the advertised declaration, so a new one
costs nothing in a session's tool listing.

The eager-validation half is spec-silent and was taken as well: check an explicitly configured
ripgrep path for existence at resolution time and report it on the health surface, so a mistyped path
is named at startup rather than at the first search. It touches retained health code, which is why it
was decided separately.

### R8 — genuine, to fix

`create`'s advertised description promises an occupied target is refused, and a note honours it while
a folder does not: `Directory.CreateDirectory` is idempotent and `VaultFiles.CreateFolder` probes
nothing, so creating an existing folder reports a creation that did not happen. Probe for the folder
before creating it and return the existing `already_exists` outcome. This makes one layer primitive
an exception to the no-preliminary-probe convention, so the remarks on `VaultFiles.CreateFolder` are
rewritten to carry why: the platform cannot report the distinction and the tool's contract requires
it. The probe is racy against a non-tool writer, which narrows the wrong answer rather than removing
it.

This departs from `Spec-Access-Layer-Capabilities.md:77`, which holds that a creating form refuses an
occupied target and that the write itself decides it, so a check and a write are never two operations.
The spec's outcome and its mechanism cannot both hold for a folder: the platform offers no
non-recursive directory create that fails when the target exists. The departure is taken because the
reason behind the rule does not apply here - checking before a file write risks destroying content
that appeared in between, while checking before a folder create risks only reporting a creation that
another writer had just made. Nothing is destroyed either way. A line for the spec-amendment step.

### R9 — genuine, to fix

`MoveResult`'s own summary promises every path-valued member reports a post-move path, and
`LinkRewrite.Path` documents itself as usable in a later call. Rewrites inside notes the move
relocates are reported where the plan found them, so those two statements are false for exactly that
subset. Translate `LinkRewrite.Path` through the relocation map when `MoveResult` is built; paths
outside the moved set are already correct and pass through. The plan keeps naming notes by their
current paths, which it must to write them, so the existing plan-level test stands and the new test
belongs at the result level.

### G1 — accepted, record the choice

Intentional. A rule of the form "a note at this path must carry this key" is a higher-level
convention, enforced at the composite or skill level rather than by the access layer or the tools.
The specs already carry the shape of that decision - `Spec-Frontmatter-Substrate.md:174` gives the
validator content and registry and nothing else, and `lint` is out of the first pass - so nothing is
amended. Record in `Design-Frontmatter-Surface.md`'s `What This Closes` that path-conditional schema
rules are deliberately not the layer's, and with it the consequence that absence of frontmatter can
never be a violation here: the validator judges what a note carries, so a note carrying nothing has
nothing to fail.

### G2 — accepted, record with G1

The same boundary. `Spec-Frontmatter-Substrate.md:97` holds that placement is explicit or an append,
with ordering conventions belonging to the tool composing the write - which is the composite or skill
level, not `edit`, which applies the operations a caller ordered and knows nothing of a note's
purpose. The old default (a new link grouped before the first folder entry) is such a convention. The
layer's affordance for it is `EntryAnchor`: a composer reads the list and anchors before or after a
named entry. Record with G1 in `Design-Frontmatter-Surface.md`.

### G3 — accepted, with two follow-ups

Markdig was originally intended to serve a role, no tool needed it, and its behavior was harvested
into the spec before the dependency was dropped. There is no oracle to restore: the rule it arbitrated
is now written down, and re-adding the package to test against it would check a rule the spec already
states. The corpus harness is N/A - this repo cannot reach a vault.

Two things come out of it. The trailing-whitespace class at `NoteSplit.cs:131` is `char.IsWhiteSpace`
where the harvested behavior trimmed `" \t\r"`, so `---` followed by a non-breaking space closes a
block here and did not before; the wider class is kept, and the comment at that line states it
explicitly and what changing it breaks, rather than leaving the choice looking incidental. And
`Spec-Frontmatter-Substrate.md:22` still names Markdig the arbiter, which is no longer true - a line
for the spec-amendment step at the end of triage.

### G4 — accepted, record the choice

The same boundary as G1 and G2. `edit` reports on the operation it performed; assessing what a note
already carries is `lint`'s, which is out of the first pass. A violation the write path collects under
a touched key is usually a sibling entry someone else wrote, and returning it on a successful call
invites the reading `Spec-Frontmatter-Substrate.md:143` names - a single-note tool treating a
non-empty list as failure - while not honouring it. Dropping the refusal is already spec-closed.
`WriteOutcome.Violations` stays unread by `EditTool` and gets a comment there saying so and why, since
a computed value nobody reads otherwise looks like an oversight.

### G5 — genuine, to fix

The `unknown_key` refusal names no alternatives and `FrontmatterVocabulary.Keys`, built for that
purpose, is consumed by nothing - `EditTool.Schema` reads only `Branches`. Carry the declared key
names as the refusal's payload, which satisfies the payload test in `Spec-Tool-Capabilities.md:169`
(the caller lifts the right key into a corrected call) and breaches no oracle rule, since the key set
is the vault's own vocabulary rather than anything filtered against authority the caller lacks.

Names only, sorted ordinally so the payload is stable across calls. No nearest-match suggestion: a
second matching rule is one more thing to own and get wrong, and the caller can read the right key
out of the list. The payload lists the declared-keys map alone - the refusal fires before namespace
prefixes matter - and the comment says so, so the omission reads as deliberate. Advertising the keys
in the schema instead was rejected: the tool listing is paid for by every session and an error
payload only by the call that erred.

### G6 — partly genuine, narrow fix

The acquire-and-proceed rule came through intact and the spec is silent on the message. Name the
vault in it: `Acquire` is given a `Vault` at the call site and reduces it to `LockName` before the
message is composed, so a reader gets a 32-hex mutex name where the vault's own name is available.
Keep the hash alongside it, since that is what correlates with another process's log line.

The old message's remediation half - that the vault may be torn, and that `git status`/`git diff`
diagnoses it - is deliberately not restored. It is a claim about repository state this build performs
no repository operations to support, and the message states a fact about the dead process rather than
a verdict on the vault.

### G7 — accepted, record the choice

The same boundary as G1, G2 and G4: that a folder ought to carry a folder note is a convention about
vault structure rather than a property of the file operation, so it is `lint`'s finding, and `lint` is
out of the first pass. `delete` and `move` did what was asked and report it. A vault declaring the
`folder_note` structural rule prices the operation instead, which is an authority refusal rather than
a warning - there is no middle state, by design.

Record the asymmetry with it, since it is the part that otherwise reads as an oversight: `move` does
maintain the invariant where maintaining it is mechanical - a folder rename carries its folder note
and `MoveResult.FolderNote` says where it landed - and stays silent where the question is a judgment
about what a folder should have.

### G8 — genuine, to fix

`ScopePath.Normalize` refuses anything `Path.IsPathRooted` accepts, so `/Projects/Widget.md` is
malformed where the old system trimmed the slash and read the note. Trim a single leading separator
instead. In a vault-relative namespace a leading slash has exactly one available meaning, and this
build already reads it that way in its other namespace: `Spec-Obsidian-Link-Behavior.md:9` measured
that a link target beginning with `/` is addressed from the vault root, so today `[[/Projects/Widget]]`
resolves while `read /Projects/Widget.md` refuses.

Two guards. Only one leading separator is trimmed - two are refused, which keeps a UNC path from
becoming a folder named for the server, and matches the editor, where `//Delta` names nothing. And
the drive-qualified refusal stays, since `C:/...` carries no leading separator and would otherwise
pass. Normalization still runs before the scope check, so containment is unchanged, and `/..` trims
to `..` and is refused as before.

This reverses the reasoning recorded for `CRD-006` and `SCP-057`, that refusing a spelling beats
silently reinterpreting it. That reasoning holds for a spelling with more than one possible meaning;
this one has a single meaning. Recorded as a reversal so it is reviewable rather than quiet. No
`suggested` payload is needed, since nothing is refused any more.

### G9 — design accepted, one description tweak

No `ignore_case` argument, as decided for ENU-027: an argument would make the layer own a second case
rule, and `ignore_case: false` alongside an inline `(?i)` in the same pattern has no sensible answer.
The engine's own flag has no such conflict.

What that decision assumed is that the capability stays reachable, which quietly assumed a caller who
reads regex documentation rather than tool schemas. An agent caller plans around what is advertised.
Name the inline case-insensitive flag in `GrepArguments.Pattern`'s description - a dozen tokens in the
tool listing, against an argument nobody finds.

### G10 — genuine, to fix through the existing pass

A folder move physically carries an attachment and nothing rewrites the embeds naming it, because the
universe is built from note enumeration. The operand perimeter is about what a tool may be given as
an operand; a wikilink is note content, and keeping a note consistent through a mutation the tool
performed is the tool's job.

Fix by giving the relocation pass file enumeration and extending the relocation map to the files the
folder carried - no second repair mechanism, since a prefix substitution of its own would be a second
answer to "does this target name a moved file?", which `Design-Core-Access-Layer.md:137` exists to
prevent. Nothing else changes: `WikilinkName.StemOf` strips only the note extension, so `diagram.png`
registers under that name alone and a bare `[[diagram]]` still cannot reach it, matching the measured
editor behavior that an extensionless target only ever names a `.md`.

Two details for the doing. Dot entries stay out of the repair pass, unlike the destruction census that
admits them. And a unique attachment basename will emit bare, which is a legal form and consistent
with how notes are emitted, but the editor's own spelling for attachments is unmeasured - a candidate
for the next measurement pass, along with the extensionless-target rule itself, which is load-bearing
here and not yet in `Spec-Obsidian-Link-Behavior.md`.

### G11 — genuine, to fix

Accept `expected_content_hash` on any edit, keeping it required only for a wholesale body
replacement. This is what `Spec-Tool-Capabilities.md:186` says - the guard is required on the one
operation that can destroy content the caller never saw, and optional elsewhere - and it makes a
guarded read-modify-write of a scalar expressible, which it is not today.

The reasoning behind the refusal is corrected with it: that a hash without a wholesale replacement
cannot change any outcome is false, since with the hash a stale note refuses and without it the edit
applies, and refusing is an outcome change. The inert-argument rule therefore never reached this
argument. The claim stands in `EditArguments.cs:55`, `Design-Tool-Vocabulary.md:192` and
`Build-Decisions.md:148`, and goes from all three.

## Observations

The parent document's `Observations` section, worked the same way. Its five bullets are numbered here
in the order they appear there, and each entry carries the grep that finds its bullet.

### O1 (the argument binder) — to fix, with G11

`grep -n -A4 'argument binder accepts unknown members' docs/Harvest-Reconciliation.md`

Set `UnmappedMemberHandling.Disallow` on `NativeToolArguments.Options`. An unknown member is almost
always a typo, and today it is dropped silently across every native tool. The refusal names the
offending member and arrives as a coded argument fault rather than an escaping binder exception, the
same treatment `EnumArgument` already gives an unrecognized enum value.

Land it with G11 rather than after: accepting `expected_content_hash` on any edit otherwise creates a
case where a typo in that member name turns a guarded write into an unguarded one silently, which is
the failure the guard exists to prevent. A client attaching metadata to an argument object would be
refused by this; that is handled if it comes up rather than designed around now.

### O2 (`.claude/guides` unreachable) — confirmed, no change

`grep -n -A4 'unreachable by every frontmatter tool' docs/Harvest-Reconciliation.md`

A dot-directory is outside the vault as far as these tools are concerned. They exist for vault
operations, and nothing in the set is meant to read or write those files, so the loss of the old
`.claude/skills`-prefix carve-out that left `guides` writable is not a loss. The code-fixed
whole-segment rule is also stronger than the prefix it replaces: there is no prefix string for a path
to climb back past, and no declaration can grant access above it.

### O3 (the reversed ranking rule) — promoted to a finding, to fix

`grep -n -A5 'link ranking rule was reversed' docs/Harvest-Reconciliation.md`

Not an observation. Measurement on 2026-08-08 established that click resolution and the autocomplete
popup use different comparators, and that the 2026-08-04 pass had measured the popup. The layer must
model click resolution, because pinning asks whether a change alters what an existing link opens and
repair asks which note a target names; neither consumer cares what a popup lists.
`Spec-Obsidian-Link-Behavior.md` now carries the corrected rule and the corrected statement about the
popup.

`WikilinkRanking.Compare` implements the popup's rule and needs rewriting to the spec's two groups:

1. A candidate inside the linking file's own folder subtree beats one outside it - that is
   `SharedDepth == FolderDepth(linkingNote)`, so no climb.
2. Within a group, the shorter relative spelling wins, measured in characters, each climb costing
   the three characters of `../` and the remainder being the candidate's segments below the shared
   prefix joined by `/`, filename and extension included.
3. Ties break on ordinal comparison of the candidates' vault paths, as now.

`SharedDepth` survives as the input that decides the group and the climb count rather than as the
ranking key, and `FolderDepth`'s current use as a secondary key goes.

Comments to correct with it. `WikilinkRanking`'s remarks date the rule to 2026-08-04; `Rank`'s summary
claims the popup orders candidates the same way, which is now known false; and
`WikilinkRankingTests`'s remarks carry the same date.

Most existing ranking tests keep their outcomes, because inside-beats-outside preserves them - but
their reasoning comments do not: `ADescentIntoTheLinkingFilesSubtreeBeatsAClimbOutOfIt` says distance
is the shared folder prefix rather than the relative path's length, which is now the wrong reason for
a right answer. Two tests are missing and are the ones that discriminate: a shallow candidate under a
long folder name against a deeper one under short names, which the current implementation gets wrong;
and two candidates outside the subtree where the one sharing less of the prefix has the shorter
spelling.

The parent document's `Improvements Taken` entry for LNK-025 records this reversal as deliberate and
spec-backed. It is now known to have been measured on the wrong surface. Nothing there is edited, but
a reader of that appendix should not take the entry at face value.

### O4 (the lock's identity derivation) — confirmed, one comment

`grep -n -A4 "identity derivation has no second party" docs/Harvest-Reconciliation.md`

Correct as built and in the right place. The derivation exists so two processes agree on one mutex
over one vault, and there is no second process yet: `src/VaultWatcher` is a stub with no `VaultShared`
reference. The five tests pin the derivation as written, which is not the same property - they would
pass identically if the scheme were unusable by the watcher, because nothing can disagree with it. A
fake second party would assert this build against itself and prove nothing further.

Name the intended second consumer in `Vault.LockName`'s remarks, which currently speak of "two
processes" abstractly, so whoever wires the watcher up finds the contract rather than inventing a
second derivation.

### O5 (`VaultWidePass` reporting) — confirmed, one remark

`grep -n -A5 'VaultWidePass. is the boundary' docs/Harvest-Reconciliation.md`

No defect. The pass reads notes the caller's grant cannot see, and none of that content escapes: the
report types carry a path, a line and the link targets, with no field note content could travel in,
and no result is logged. What the observation found is a remark deferring the constraint to "above
this type" without saying what was deferred.

Reword it to place responsibility rather than shape: this pass hands out access the caller's grant
does not have, and what may be reported out of it is the consumer's to decide. Naming today's report
types would go stale the first time a new consumer appears, and stating a narrow contract would read
as binding on consumers it was never written for.

## Containment properties with no test

The parent document's last section, four bullets, numbered here in the order they appear there. Read
them all at once:

```bash
sed -n '/^## Containment properties with no test/,/^## Improvements Taken/p' docs/Harvest-Reconciliation.md
```

Three are test gaps and one is not.

### C1-C3 — to assert in the suite sweep

Highest-value entries for that step, because each breaks silently. `delete`'s census runs vault-wide
but no delete test narrows scope to prove it; `move`'s repair is not scope-limited but no move-level
test drives it under a narrow grant; and search arguments reach the binary without shell
interpretation, with a leading-hyphen pattern matched as data. Each asserts that something deliberately
unscoped stays unscoped, which is what regresses when a later reader adds a scope check for tidiness.

### C4 (op-class comparison) — to fix

Not a test gap. `Grant.Allows` is a bare `Contains` over an `IReadOnlySet<string>`, so the
comparison's case sensitivity is whichever comparer the producer chose. Every producer chooses a
case-insensitive one today, making the gate consistent by coincidence of construction rather than by
rule; a new producer taking the ordinal default would silently stop matching a floor spelled in
another case. Make the record own the comparison so no producer can get it wrong. The end result is
case-insensitive, which is what the gate already does, so nothing changes behaviourally.

`Grant` is retained code and `Spec-Auth-Requirements.md:9` fixes the grant model, but the operator
took this into the reconciliation's fix batch rather than as a sidequest, and editing the existing
code for it is agreed.
