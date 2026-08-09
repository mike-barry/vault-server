# Cross-Platform

This repository's position on platform support. Read it before writing code, a
test or a comment that treats more than one operating system as possible.

## The position

**Windows is the platform.** Not a default, not the platform it happens to be
developed on - the one it is built for and the only one it is claimed to run on.

That reaches further than a build target, because the filesystem is
case-insensitive and the layer is written on that premise throughout. `Foo.md`
and `foo.md` name one file. Path equality ignores case, so a path is contained,
excluded or in scope regardless of how it is spelled, and a caller cannot reach
an excluded file by changing its capitalization. A frontmatter identity naming a
file compares the same way, matching how the filesystem and the editor resolve
it. And a rename that changes only case names a destination that already exists,
which is why that case is allowed to overwrite. Those are not four decisions
about four operations; they are one premise showing up in four places.

**Cross-platform is deferred, not abandoned.** Nothing forecloses a port. What
is refused is supporting one in advance.

Supporting it in advance is the expensive position, for a reason that does not
depend on how much code the accommodations amount to. Nothing runs this anywhere
but Windows, so nothing exercises the other side of any branch and no test can be
written that would. An accommodation nothing exercises is a claim, not a
capability: it is as likely to be wrong as right, and no one finds out either
way. Meanwhile every reader pays for it. Each such site has to be understood
twice, and each question asked about it - is this a bug, is this dead, does this
still hold - is answered by working through both platforms and concluding that
only one of them was ever real.

What the position gives up concretely: running the server in a Linux container.

## What not to write

Four things. Each of them looks, in isolation, like ordinary defensive coding,
which is why they are named rather than left to judgement.

- **No branch on the operating system.** No `OperatingSystem.IsWindows()`, no
  `RuntimeInformation.IsOSPlatform`, no `#if` on a platform symbol, and no code
  path that answers differently depending on where it runs. Write the Windows
  answer as the answer.
- **No second failure classification** kept because another platform reports a
  condition differently - a second errno beside a Windows error code, or a second
  exception type mapped to the same outcome. Classify what this platform raises.
- **No test gated by or skipped on an operating system**, and no arrangement
  chosen so that it behaves identically on two. Arrange for the Windows
  behaviour and assert it directly. A skipped test is worse than an absent one:
  it reports as coverage while guarding nothing.
- **No comment hedging about another platform** - "on a case-sensitive
  filesystem", "on other platforms this may", "Windows and Linux disagree here".
  A hedge weighing what two platforms might do documents a decision that was
  never made. Where the platform fact is worth stating, state it plainly as a
  fact about this platform.

Three things look like portability code and are not, so leave them alone.
Emitted paths are vault-relative with one separator convention because callers
quote them back into later calls unchanged - that is a promise to the caller, not
a concession to another filesystem. Notes are stored with LF terminators because
that is how they are stored. And using the framework's own path APIs correctly is
not an accommodation.

## Breadcrumbs

Removing an accommodation is what makes a platform dependency invisible. What is
left reads as ordinary correct code, and nothing on the page says it is correct
only here. A breadcrumb is how such a site says so.

A breadcrumb is one sentence at the code site, opening with the word `Windows:`,
stating what this platform does and what a port would have to establish. It is
prose in the ordinary register - not a marker. It carries no `TODO` and refers to
no document or task, both of which move or are archived while the code stays.
Opening with the one word is what makes the set findable without any of that:

```
rg "Windows:" src
```

**A site earns one where the platform dependency would otherwise be invisible.**
That is the whole test. It is not met by every site the premise touches. Case
insensitivity reaches most of the access layer, and a note at every comparison is
noise that trains a reader to skip all of them. Where one site is the root of a
premise many others inherit, the root carries the breadcrumb and the inheritors
do not. A site whose platform dependence is plain from what it calls needs none,
and neither does a risk that a compiler, a test or the framework's own API would
catch.

## If the position is revisited

A port is not a matter of restoring the branches. It is the audit this position
postpones, and it starts from the breadcrumbs - which are the sites known to be
invisible, not a complete inventory. Case-insensitive path equality is the first
thing to establish, because it is assumed rather than checked at most of the
places that depend on it. Beyond that, the port needs something that does not
exist today and is the real prerequisite: a way to run the test suite on the
other platform. Until that exists, any claim about how this code behaves there is
untested by construction.
