# task-009: Make Windows the stated platform, and take out what only served portability

**Goal:** The specs state that this system runs on Windows, and no code, test or comment carries a branch, a second classification or a hedge that exists only to accommodate another platform.

## Context

The build is already Windows-only in all but name, and the accommodations that remain do not add up to portability. They cost attention without buying the capability.

`docs/spec/Spec-Access-Layer-Capabilities.md:Moving` states the case-only-rename carve-out on the premise of a case-insensitive filesystem. `VaultPath.Equals` is `OrdinalIgnoreCase` throughout, `docs/spec/Spec-Tool-Capabilities.md:Path Policy` makes exclusion matching case-insensitive so a caller cannot reach an excluded file by spelling it differently, and `docs/spec/Spec-Frontmatter-Registry.md:Entry Schemas` makes an identity naming a file or a wikilink compare case-insensitively "matching how the filesystem and the editor resolve it". On a case-sensitive volume the layer is wrong in places that have nothing to do with any one operation, so the premise is systemic rather than local.

The cost of the current halfway position is measurable. In the code review alone: a case-only-rename finding took a full review and refutation cycle to conclude the premise excludes it; `VaultFiles.PreflightCreate` carries a documented gap solely because Windows and Linux disagree about creating a file over a directory; and the test-suite audit gated a Windows-only path case. Each resolved as "not really a problem", which is the tax this task removes.

**Cross-platform is an eventual goal, not an abandoned one.** What is wrong is supporting it out of the gate with no way to test it, and complicating the code for a capability nothing exercises. Taking it up later is a deliberate choice with its own audit. This task stops paying for it in advance; it does not foreclose it.

**So the risks that are not discoverable by reading get a breadcrumb.** Removing a branch is what makes a platform dependency invisible: the code afterwards looks like ordinary correct code, and the fact that it is correct only here is nowhere. A future port should not have to rediscover those sites by finding the bugs. See "Breadcrumbs" below for what earns one and what a breadcrumb may say.

**What is given up:** running the MCP server in a Linux container.

## Scope

**In scope** — accommodation, which is what this removes:

- Platform branching in code: `RuntimeInformation`/`OperatingSystem` checks, `#if` on platform, and any path that answers differently by platform.
- A second failure classification kept because two platforms raise different exceptions for one condition.
- Tests gated by, or skipped on, an operating system, and test arrangements chosen to behave the same on both.
- Comments hedging about "on other platforms", "on a case-sensitive filesystem", or what a platform "may" do.

**Out of scope** — these look like portability code and are not:

- **Vault-relative paths with one separator convention.** A spec guarantee (`docs/spec/Spec-Vault-Access-Design.md:Guarantees To Callers`), so results quote back into later calls unchanged. Not portability.
- **LF line terminators.** Notes are stored that way and the repository normalizes on checkout (`docs/spec/Spec-Vault-Access-Design.md:Fixed Constraints`). Nothing to do with the platform.
- **`Path.Combine`, `Path.GetFullPath` and the framework's own path APIs.** Using the platform's API correctly is not an accommodation.
- **The `Global\` mutex prefix.** It stays, and stops being described as the Windows half of a two-platform rule.

## Breadcrumbs

A breadcrumb is one sentence at a code site, opening with `Windows:`, saying what this platform does and what a port would have to establish. It is prose in the ordinary register, not a marker: no `TODO`, no reference to this task, this file, or any document, all of which archive or rot while the code stays. Opening with the one word makes the set greppable without any of that.

**A site earns one only where the dependency would otherwise be invisible.** Four do:

- **`VaultFiles.MoveFile`** - `overwrite` is decided by case-insensitive path equality, so a case-only rename replaces its own destination. On a case-sensitive filesystem the destination is a different file and the move destroys it silently. The most valuable of the four, and the one the code review raised before the premise ruled it out.
- **`VaultPath.Equals`** - the root of that premise. One breadcrumb here is why every other comparison site needs none.
- **`VaultFiles.PreflightCreate`** - once it decides a directory occupying a note's target, the divergence it was avoiding stops being visible anywhere.
- **The mutex lock name** - `Global\` scopes the mutex across sessions on Windows, and a port needs whatever does that on its platform, or the watcher and the server take different locks.

**Nothing else does.** The case-insensitivity premise reaches most of the layer, and a note at every comparison is noise that trains a reader to skip all of them. A breadcrumb is not for a risk a compiler, a test or the framework's own API would catch, and not for a site whose platform dependence is plain from what it calls.

## Acceptance criteria

- [ ] `docs/spec/Spec-Vault-Access-Design.md:Fixed Constraints` states Windows as the platform, beside the existing constraint that the vault is a plain directory tree with no symbolic links, junctions or reparse points. It says what follows from it — a case-insensitive filesystem, which several companion specs already assume — so no companion has to restate it.
- [ ] `docs/spec/Spec-Vault-Access-Design.md:The Mutex Lock Name` drops the non-Windows form. The derivation is otherwise unchanged, since it is a cross-process contract with `VaultWatcher`.
- [ ] Every other spec and design document is checked for text that hedges about a platform, and rewritten where it does. A companion that assumed case-insensitivity implicitly may now cite the constraint instead of restating the premise.
- [ ] No file under `src/` branches on the operating system, and no member classifies one condition two ways because two platforms raise different exceptions.
- [ ] No test is gated by or skipped on an operating system. Where a test was arranged to behave identically on both, it may be arranged for the Windows behaviour and assert it directly.
- [ ] `VaultFiles.PreflightCreate` decides a directory occupying a note's target, which it currently leaves to the write because the two platforms answer differently. Its `<remarks>` loses that paragraph and gains its breadcrumb.
- [ ] No comment under `src/`, `tests/` or `conventions/` hedges about another platform. A hedge weighing what two platforms might do is not a breadcrumb: it goes, and the four sites above state the Windows fact plainly instead.
- [ ] The four sites under "Breadcrumbs" carry one, and no other site does. Each is one sentence opening with `Windows:`, stating the platform fact and what a port would need to establish, referencing no document and no task.
- [ ] Nothing in the "Out of scope" list above was changed.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

**Run this before [task-010](task-010-conformance-audit.md).** If the specs state Windows while the code still branches, the conformance audit raises a finding per branch and the same decision is triaged many times over. It also belongs before [task-011](task-011-comment-sweep.md), so a hedging comment is rewritten once rather than swept and then deleted.

The test-suite audit gated a Windows-only path case. That gate is one of the things this task removes, and is a place to start looking.
