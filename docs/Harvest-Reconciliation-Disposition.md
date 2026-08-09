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

`C#` is assigned here. The parent's `Containment properties with no test` section is unlabelled
bullets, numbered here in the order they appear there.

A finding's own case IDs resolve into `harvest/` by their three-letter prefix; the parent document's
"Looking something up" section has those commands.

## The fix batch

The fix step is done: every finding it covered is closed, and its code, tests, design text and spec
amendments have landed. What is left below belongs to the suite sweep.

One warning about the parent document's own appendix, which the sweep clears along with the rest.
Its `Improvements Taken` entries for LNK-004, LNK-025 and CRD-006 each record a reason that no
longer holds: LNK-025 defends the link-ranking reversal as spec-backed and cites a line range in
`WikilinkRanking.cs` that says something else, LNK-004 says the link universe indexes notes only,
and CRD-006 says a leading separator is refused rather than trimmed. The judgments they defend still
stand; their stated reasons do not. The appendix is a verbatim audit trail and is not edited, so a
reader should not take those three at face value.

## Containment properties with no test

The parent document's last section, three bullets, numbered here in the order they appear there.
Read them all at once:

```bash
sed -n '/^## Containment properties with no test/,/^## Improvements Taken/p' docs/Harvest-Reconciliation.md
```

All three are test gaps.

### C1-C3 — to assert in the suite sweep

Highest-value entries for that step, because each breaks silently. `delete`'s census runs vault-wide
but no delete test narrows scope to prove it; `move`'s repair is not scope-limited but no move-level
test drives it under a narrow grant; and search arguments reach the binary without shell
interpretation, with a leading-hyphen pattern matched as data. Each asserts that something deliberately
unscoped stays unscoped, which is what regresses when a later reader adds a scope check for tidiness.
