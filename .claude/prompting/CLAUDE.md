# Prompting

Files in this folder are inert unless the root `CLAUDE.md` imports them. What
follows describes them and is not itself instruction.

Each file holds prompt text and nothing else. An imported file enters the
context window whole on every session, so provenance and commentary are kept
here instead of in it.

A file may sit here unimported. The root `CLAUDE.md` is the only statement of
what this repository loads. Two files may rule on the same thing, but only one
of them may be imported, because neither yields to the other.

## Entries

An entry is one of the sections below, named for the prompt file it documents.
It records what the file is there to change about an agent's behavior, and where
its text came from. Text written here says so and needs nothing further. Text
from elsewhere names its source precisely enough to find again, says whether it
is quoted or adapted, and gives each local change or addition with its reason.
An entry records nothing else.

## Response-Style.md

Claude Opus 5 writes longer conversational responses than earlier models and
narrates more of what it is doing while it works. This file shortens both.

The two rule paragraphs are quoted verbatim from the "Response length and
verbosity" and "User-facing progress updates" sections of Anthropic's Opus 5
prompting guide:

https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5

The opening scope line is not from the guide. It confines the file to
conversation, so that nothing in it reads as a rule about the length or shape of
a comment, a document, or a task file.

## Prose-Style.md

Claude Opus 5 reaches for a set of constructions that make reference prose hard
to read: the colon used as a conjunction, the "not X, it is Y" antithesis, a
reason argued instead of stated, importance asserted rather than shown, and
terms coined for a single passage. The rules are model-neutral and worth keeping
whichever model runs.

The text was written here, apart from the colon rule and the ban on thesis
paragraphs. Those came out of `docs/conventions/Code-Comments.md`, which governed
C# comments alone while the same constructions were appearing in documents and
task files. That document now points here for both, and for how a reason is
worded once it decides one is worth keeping.
