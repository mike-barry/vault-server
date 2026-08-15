# Prose Style

These rules govern every sentence written for a human to read: a comment in any
language, a document, a task file, a commit message, and a reply in
conversation. A sentence is not exempt because the file holding it is code.

## A colon introduces a list, not a reason

A colon may introduce an enumeration of what was just named. It may not stand in
for "because", "so", or "and". Where it welds a claim to its justification,
write the connective, or split it into two sentences.

> **No:** `The check runs first: a later one would already have touched the file.`
> **Yes:** `The check runs first, because a later one would already have touched the file.`

> **Fine:** `The result is one of three things: a match, no match, or an error.`

## No rhetorical structure

State a claim directly rather than building to it. No antithesis of the form
"this is not X, it is Y". No sentence fragment standing in for a sentence. No
bolded claim, no manifesto structure, no rhetorical parallelism. Emphasis is
almost never needed.

> **No:** `Not a detail. A design decision.`
> **Yes:** `This is a design decision.`

## A reason is stated, not argued

Where a reason is worth keeping, give it in one sentence, as a fact. Do not
build a case for it or anticipate the objections.

> **No:** `The retry count is three rather than five, which may look arbitrary, but a higher number would leave the caller waiting past its own timeout, and failing early is the better of the two outcomes.`
> **Yes:** `The retry count is three, because five would push the caller past its own timeout.`

## Importance is shown, not asserted

Say what breaks rather than that something matters. A sentence whose only work
is to mark the next one as important can be deleted without loss.

> **No:** `This ordering is load-bearing. The check runs before the write.`
> **Yes:** `The check runs before the write, because a later check would see its own result.`

## Do not coin a term

Use the words the domain already uses. A term invented for one passage reads as
though it is defined somewhere, and the reader spends time looking for the
definition.

> **No:** `The registry is refreshed at every enrollment boundary.`
> **Yes:** `The registry is refreshed whenever a script is added or removed.`

## The em dash is not this repository's punctuation

Use a comma, a full stop, or parentheses. Where a dash is genuinely the right
mark, write a spaced hyphen, which is what every document here already uses.
