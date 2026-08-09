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

`docs/conventions/Cross-Platform.md` defines what a breadcrumb is, the test a site has to meet to earn one, and why most platform-dependent sites do not. That definition governs; it is not restated here. What this task adds is the sites already known to meet it.

**Four sites are known to earn one.** They were identified before the work started, so they are the floor and not the ceiling — a site met during the work that passes the test above earns one on the same footing:

- **`VaultFiles.MoveFile`** - `overwrite` is decided by case-insensitive path equality, so a case-only rename replaces its own destination. On a case-sensitive filesystem the destination is a different file and the move destroys it silently. The most valuable of the four, and the one the code review raised before the premise ruled it out.
- **`VaultPath.Equals`** - the root of that premise. One breadcrumb here is why every other comparison site needs none.
- **`VaultFiles.PreflightCreate`** - once it decides a directory occupying a note's target, the divergence it was avoiding stops being visible anywhere.
- **The mutex lock name** - `Global\` scopes the mutex across sessions on Windows, and a port needs whatever does that on its platform, or the watcher and the server take different locks.

A breadcrumb given to a site beyond these four is recorded in Notes, with the reason it met the test. That keeps the set reviewable without capping it.

## Acceptance criteria

- [x] `docs/spec/Spec-Vault-Access-Design.md:Fixed Constraints` states Windows as the platform, beside the existing constraint that the vault is a plain directory tree with no symbolic links, junctions or reparse points. It says what follows from it — a case-insensitive filesystem, which several companion specs already assume — so no companion has to restate it.
- [x] `docs/spec/Spec-Vault-Access-Design.md:The Mutex Lock Name` drops the non-Windows form. The derivation is otherwise unchanged, since it is a cross-process contract with `VaultWatcher`.
- [x] Every other spec and design document is checked for text that hedges about a platform, and rewritten where it does. A companion that assumed case-insensitivity implicitly may now cite the constraint instead of restating the premise.
- [x] No file under `src/` branches on the operating system, and no member classifies one condition two ways because two platforms raise different exceptions.
- [x] No test is gated by or skipped on an operating system. Where a test was arranged to behave identically on both, it may be arranged for the Windows behaviour and assert it directly.
- [x] `VaultFiles.PreflightCreate` decides a directory occupying a note's target, which it currently leaves to the write because the two platforms answer differently. Its `<remarks>` loses that paragraph and gains its breadcrumb.
- [x] No comment under `src/` or `tests/` hedges about another platform, and neither does an example comment in `docs/conventions/`. A hedge weighing what two platforms might do is not a breadcrumb: it goes, and the four sites above state the Windows fact plainly instead.
- [x] Every site meeting the breadcrumb test in `docs/conventions/Cross-Platform.md` carries one, including the four named under "Breadcrumbs". Any site beyond those four is recorded in Notes with the reason it met the test.
- [x] Nothing under `src/` or `tests/` contradicts `docs/conventions/Cross-Platform.md`, which already states the position this task carries out.
- [x] Nothing in the "Out of scope" list above was changed.
- [x] `dotnet build --no-incremental` clean, `dotnet test` green in `VaultShared.Tests` and `VaultCli.Tests`. `VaultMcpServer.Tests` cannot run on this machine: Smart App Control blocks `ModelContextProtocol.dll` by hash, which a rebuild re-triggers. Pre-existing and unrelated to this work.

## Notes

**Run this before [task-010](task-010-conformance-audit.md).** If the specs state Windows while the code still branches, the conformance audit raises a finding per branch and the same decision is triaged many times over. It also belongs before [task-011](task-011-comment-sweep.md), so a hedging comment is rewritten once rather than swept and then deleted.

The test-suite audit gated a Windows-only path case. That gate is one of the things this task removes, and is a place to start looking.

**No site beyond the four earned a breadcrumb.** The premise reaches many comparison sites, and each of them inherits from `VaultPath.Equals`, which is where the one breadcrumb sits.

**The `PreflightCreate` criterion pulled a behaviour change in with it, decided by the operator in session.** Deciding a directory at the target left `Create` answering the same condition differently — `Failed` with an access-denied message, since Windows raises `UnauthorizedAccessException` rather than an already-exists error code — and two answers for one condition is what the removed `<remarks>` paragraph existed to avoid. The operator's ruling was that creation refuses a name that is already taken and what occupies it makes no difference. So:

- `Create` now answers by calling `PreflightCreate`, which probes for a file and a folder alike. The atomic `FileMode.CreateNew` write stays behind it and refuses a file that appears in the window, so the probe cannot lose content and the raced call still answers `AlreadyExists`.
- `CreateFolder` gained the symmetric hole's fix: a *file* occupying a folder's target answered `Failed`, because Windows raises `ERROR_ALREADY_EXISTS` there but the catch went straight to `Classify`, which never consults `IsAlreadyExists`. It now probes for both kinds and carries the missing catch. This half was never a platform accommodation — it is a pre-existing inconsistency the ruling covers, and it was fixed here rather than left standing beside the other half.
- Both moves were already consistent: `File.Move` onto a directory and `Directory.Move` onto a file both raise `ERROR_ALREADY_EXISTS`, so `DestinationOccupied` was already the answer whatever the occupant. Nothing changed there.
- `CreateGitKeep` cannot route through `PreflightCreate` — `.gitkeep` deliberately fails the note grammar, so the preflight would call it `InvalidName`. Its own catches already answer a file occupant correctly, and a *directory* named `.gitkeep` is not reachable through this layer. Left alone.
- Amended in the same commit: `Spec-Access-Layer-Capabilities.md:Writing` (the check and the write are no longer required to be one operation, and the reason is that the write is atomic), the folder-creation paragraph that claimed to be the only place a check and a write are two operations, and the matching text in `Design-Core-Access-Layer.md` including two `What This Closes` entries.

**One test property is now uncovered, and is recorded where it was lost.** `CreateToolTests` asserted that pins are written before the note by using a directory at the note's target — the one arrangement that passed the preflight and then failed at the write. The preflight now refuses it, so that arrangement is gone and no other reaches one write and not the other without a seam existing only for a test. The test was rewritten to assert what the change makes true instead — an occupied target refuses before any bystander is pinned, where previously the pass rewrote bystanders and *then* failed — and the lost ordering property is stated in its `<remarks>`.

**Left in place deliberately.** `docs/Build-Decisions.md` carries platform hedges (a Unix pre-check on move, "idempotent on every platform") and was left untouched: the operator scoped the sweep to specs, design, `src/`, `tests/` and `docs/conventions/`. Several entries there now describe the pre-change behaviour of creation.
