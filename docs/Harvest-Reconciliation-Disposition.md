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

`C#` is assigned here. The parent's `Containment properties with no test` section is unlabelled
bullets, numbered here in the order they appear there.

A finding's own case IDs resolve into `harvest/` by their three-letter prefix; the parent document's
"Looking something up" section has those commands.

## The fix batch

What triage produced, for whoever works the fix step. Every item is detailed in its entry below.

Code changes, with their couplings:

- **The vault named in the inherited-lock message** - G6
- **The inline case-insensitive flag named in `grep`'s pattern description** - G9
- **`Grant.Allows` owning its comparison** - C4

Carried to the suite sweep: C1-C3, which are test gaps rather than code defects and are that step's
highest-value entries.

One correction to the parent document's own appendix: its `Improvements Taken` entry for LNK-025
records the link-ranking reversal as deliberate and spec-backed, and cites a line range in
`WikilinkRanking.cs` that no longer says what the entry claims. That reversal was measured on the
autocomplete popup rather than on what following a link does, so the entry's judgment does not hold.
The appendix is not edited; a reader should not take that entry at face value.

## Dispositions

### G6 — partly genuine, narrow fix

The acquire-and-proceed rule came through intact and the spec is silent on the message. Name the
vault in it: `Acquire` is given a `Vault` at the call site and reduces it to `LockName` before the
message is composed, so a reader gets a 32-hex mutex name where the vault's own name is available.
Keep the hash alongside it, since that is what correlates with another process's log line.

The old message's remediation half - that the vault may be torn, and that `git status`/`git diff`
diagnoses it - is deliberately not restored. It is a claim about repository state this build performs
no repository operations to support, and the message states a fact about the dead process rather than
a verdict on the vault.

### G9 — design accepted, one description tweak

No `ignore_case` argument, as decided for ENU-027: an argument would make the layer own a second case
rule, and `ignore_case: false` alongside an inline `(?i)` in the same pattern has no sensible answer.
The engine's own flag has no such conflict.

What that decision assumed is that the capability stays reachable, which quietly assumed a caller who
reads regex documentation rather than tool schemas. An agent caller plans around what is advertised.
Name the inline case-insensitive flag in `GrepArguments.Pattern`'s description - a dozen tokens in the
tool listing, against an argument nobody finds.

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
