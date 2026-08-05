# Reconciliation: The frontmatter key registry and entry schemas

Harvest source: `05-Registry-Schema-Harvest.md` (73 cases)
Implementation read: `src/VaultShared/Frontmatter/FrontmatterRegistry.cs`, `FrontmatterRegistries.cs`,
`FrontmatterRegistryLoader.cs`, `FrontmatterVocabulary.cs`, `ValueConstraint.cs`, `EntryResolver.cs`,
`FrontmatterValidator.cs`, `FrontmatterChecks.cs`, `FrontmatterWrite.cs`, `FrontmatterOperation.cs`,
`FrontmatterTree.cs`, `RegistryState.cs`, `Violation.cs`; `src/VaultShared/Scope/PathPolicy.cs`,
`ScopePath.cs`; `src/VaultShared/Tools/EditTool.cs`, `EditArguments.cs`; `frontmatter-registry.json`
Tests read: `tests/VaultShared.Tests/Frontmatter/FrontmatterValidatorTests.cs`,
`FrontmatterRefusalTests.cs`, `FrontmatterAuthorityTests.cs`, `ValueConstraintTests.cs`,
`FrontmatterRegistryLoaderTests.cs`, `SyntheticVocabulary.cs`
Design/spec consulted: `docs/design/Design-Frontmatter-Surface.md` (Advertising The Vocabulary;
What This Closes), `docs/spec/Spec-Frontmatter-Registry.md`, `docs/spec/Spec-Frontmatter-Substrate.md`,
`docs/spec/Spec-Tool-Capabilities.md:Path Policy`, `docs/Build-Decisions.md`, `docs/Plan.md`

## Summary

Cases: 73. MATCH 47 · IMPROVED 7 · N/A 9 · REGRESSION 2 · GAP 8 · UNRESOLVED 0

The seven keys' own semantics survived the move from a hardcoded table to a declaration. Every rule
the old registry applied to a value is expressible in the new constraint set, the refusal ordering is
preserved (control characters before the key's own constraint), key lookup is still case-insensitive
and canonicalizing, and the whole-tree extent that aborts a `create` on one bad key is intact.

The three behaviors the task singled out all hold:

- **REG-010 / REG-011.** "Is agentic" and "is allowed" are genuinely two answers.
  `FrontmatterWrite.KeyAuthority` (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:637-653`) prices a
  key by walking `registry.Namespaces` **whether or not the key is declared**, while
  `registry.Keys.TryGetValue` is what admits it. A namespace match can only add. Nothing merges them.
- **REG-023 / REG-052.** The old two-layer split is gone, and it is gone in the safe direction: a
  block-list key may not declare a value constraint at all (loader refuses it,
  `FrontmatterRegistryLoader.cs:130`), and its entries are always walked against the entry schema by
  the same `CheckKey` the write path runs. The "malformed value slips past the registry" shape cannot
  arise because there is no registry pass that skips the schema.
- **REG-027 / REG-028.** A supplied block is validated key-by-key and throws on the first finding
  before anything is emitted (`FrontmatterWrite.cs:225-233`); operations are all validated before any
  is applied (`FrontmatterWrite.cs:65-66`). A smuggled undeclared `agent.*` key is refused
  `UnknownKey`, and holding the elevated class does not admit it.

The exclusion path (REG-033..REG-041) is the one area where the harvest and the code are talking about
different rules. The rewrite has no exclusion *list* and no path normalizer: `Spec-Tool-Capabilities.md:84`
fixes "nothing under a dot-directory is reachable at all" in code, and `RefusePathExclusion`
(`FrontmatterWrite.cs:168-177`) implements exactly that per segment. That subsumes REG-034..REG-037
correctly — a `..` can only remove segments, never introduce a dot segment, so the climb-back-in case
cannot be evaded — and it deliberately over-matches REG-038/039/040. Those are dismissed to the spec,
but the operator should be aware the trade is real: REG-039 recorded that `.claude/guides/*.md` are
**vault notes carrying vault frontmatter**, and no frontmatter tool can now touch them.

**The one thing to look at first is the Tasks.md directive requirement (REG-066..REG-072).** It is the
only rule in the whole area with no counterpart of any kind, and the only rule where *absence* was the
violation. Nothing in the specs, the design documents or `Build-Decisions.md` mentions it.

The other two findings are small and both cheap to close: a required entry field can now be satisfied
by an empty value, and the unknown-key refusal no longer tells the caller what the vocabulary is.

## Findings

### GAP — REG-066, REG-067, REG-068, REG-069, REG-070, REG-071, REG-072 — The Tasks.md directive requirement is absent entirely

- **Old behavior:** The entry-schema validator took an optional vault-relative path and, where the
  note's filename was `Tasks.md` and no `Archive` segment appeared anywhere in the path, required an
  `agent.instructions` entry carrying `use_skill: vault-tasks`. Its absence was
  `tasks_missing_use_skill`, reported against `agent.instructions`. REG-068 is the sharp edge: the rule
  fired on a `Tasks.md` **with no frontmatter block at all**, the sole exception to "no frontmatter is
  vacuously clean" (REG-044). Filename matching was case-insensitive and separator-folded; the check
  was skipped when no path was supplied.
- **New behavior:** `FrontmatterValidator.Validate(content, registry)`
  (`src/VaultShared/Frontmatter/FrontmatterValidator.cs:32`) takes no path, and its own summary states
  the property positively: "It takes the note's content and the declaration and needs nothing else - no
  path, no filesystem, no consumer configuration." A note with no block produces no violations
  (`FrontmatterValidator.cs:37,51-53` — `tree.Keys` is empty, so the loop body never runs), with no
  exception for any filename. The declaration language has no vocabulary for a conditional
  requirement either: `KeyDeclaration` and `EntryVariant` describe shape and authority only, and
  nothing in `FrontmatterRegistryLoader` reads a path predicate. There is no `Archive` handling
  anywhere in `src/VaultShared/Frontmatter/`.
- **What is lost:** The one rule in the area that could detect a *missing* convention rather than a
  malformed one. A `Projects/Widget/Tasks.md` that carries no `vault-tasks` directive — including one
  that carries no frontmatter whatsoever — is now clean by every check the system has. Any agent
  routing that depends on the directive being present degrades silently, note by note, with nothing
  reporting it. The `Archive` exemption is lost with it, but that only mattered because the rule
  existed.
- **Covered by a test?** No test asserts this; there is nothing to assert. `ANoteWithNoBlockHasNoViolations`
  (`tests/VaultShared.Tests/Frontmatter/FrontmatterValidatorTests.cs:24`) asserts the opposite arm
  unconditionally.
- **Recorded anywhere?** Nothing records this. Grepping `docs/spec/` for "Tasks" returns no hit;
  `Design-Frontmatter-Surface.md:What This Closes` does not mention a path-conditional check;
  `Build-Decisions.md` does not mention it. Worth noting for scoping: `docs/Plan.md` shows no sweeping
  or lint tool was built, so the validator currently has **no production consumer at all**
  (`FrontmatterValidator.Validate` is called only from its own test file). That may be why the rule was
  never missed — but the rule lived in the schema validator, which is built, not in the tool that
  would have called it.

### GAP — REG-014 — The unknown-key refusal no longer names the vocabulary, and nothing else advertises it

- **Old behavior:** Refusing an unregistered key produced a reason that *enumerated the known keys*
  (the test asserted the message contains `doc.created`). The harvest records the self-describing
  property as the semantic part and the exact prose as artifact: the refusal told the caller what it
  could have written instead.
- **New behavior:** The refusal is `No declaration recognizes the key "{key}".`
  (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:267`, and `FrontmatterChecks.cs:111` for the
  violation form). It names nothing else. The intended replacement channel does not carry it either:
  `FrontmatterVocabulary.Of` builds a `KeyAdvertisement` list (`FrontmatterVocabulary.cs:47-53`), but
  `EditTool.Schema` (`src/VaultShared/Tools/EditTool.cs:113-129`) consumes **only** `vocabulary.Branches`,
  and the `key` argument is advertised as a bare string with the description "The frontmatter key being
  mutated." (`src/VaultShared/Tools/EditArguments.cs:166`). `FrontmatterVocabulary.Keys` has no consumer
  anywhere in `src/`.
- **What is lost:** An agent that guesses a key wrong has no route to the right one. It cannot read the
  declaration file (it lives outside the vault, at the configuration trust tier, by design), the
  refusal does not list the alternatives, and the advertised schema does not carry them. The old
  system closed that loop in the refusal itself. The consequence is concrete on a vault whose
  declaration the agent has never seen: repeated refusals with no information gradient.
- **Covered by a test?** `UnknownKey_AnUndeclaredKeyIsRefusedBeforeItsShapeMatters`
  (`tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs:162`) asserts the reason and the key
  attribution only. Nothing asserts the message is self-describing, and nothing asserts the key list
  reaches a client.
- **Recorded anywhere?** Nothing records dropping it. `Design-Frontmatter-Surface.md:Advertising The
  Vocabulary` describes the projection as "one branch per entry variant" and says nothing about the key
  list, so the `KeyAdvertisement` half looks built-and-unwired rather than deliberately withheld.

### REGRESSION — REG-056, REG-063 — A required entry field is satisfied by an empty or whitespace value

- **Old behavior:** Required-field checks tested *whitespace-emptiness*, not presence. The harvest
  records this on both required-field rules: on REG-056, "Implementation tests for whitespace-emptiness,
  so `description: "   "` also violates"; on REG-063, "Implementation tests whitespace-emptiness, so a
  blank `when` also violates." A nav entry carrying `description:` with nothing under it was
  `missing_description`.
- **New behavior:** The check is presence-only:
  `if (field.Required && entry.Field(name) is null)` (`src/VaultShared/Frontmatter/FrontmatterChecks.cs:211`).
  A field present with no value is not null — `FrontmatterEntry.Field` returns
  `scalar.Value ?? string.Empty` (`FrontmatterTree.cs:356`) and `EntryPayload.Mapping.Field` returns the
  trimmed caller value (`FrontmatterOperation.cs:94-95,108`). The value then meets `StringConstraint`,
  which admits everything (`ValueConstraint.cs:214`). On the write path the entry payload's *name* is
  emptiness-tested but its *value* is not (`FrontmatterWrite.cs:336-340`), so
  `add_entry nav.contents {link: "[[X]]", description: "  "}` trims to `description: ""` and is accepted.
  Note the asymmetry within the same file: a declared **scalar key** with an empty value *is* caught, by
  `OperationText.IsEmpty` at `FrontmatterChecks.cs:127`. The test exists and is simply not applied to
  required entry fields.
- **What is lost:** "Required" no longer means "carries content". An entry can satisfy every declared
  requirement while its description, or a `use_skill` entry's `when`, is blank — accepted by the write
  path and reported clean by the validator. The declaration cannot recover the guarantee either: the
  only constraint kind that could reject an empty string is `charclass` with `min >= 1`, and no
  character class admits ordinary prose (no punctuation, no non-ASCII), so a free-text field has no way
  to declare itself non-empty.
- **Covered by a test?** No test asserts this in either direction. `AMissingRequiredField`
  (`tests/VaultShared.Tests/Frontmatter/FrontmatterValidatorTests.cs:123`) uses a field that is wholly
  absent; no test supplies a present-but-empty required field.
- **Recorded anywhere?** Partially, and not for this case. `docs/Build-Decisions.md:98` records "The
  emptiness test binds addressing values, not every value… A scalar field's value is not: an empty
  string is a legitimate value under a string constraint, and what bounds it is the declared
  constraint." That decision is about *operation arguments*; it does not address what `required` on an
  entry field means, and the "what bounds it is the declared constraint" escape does not hold here,
  because no declarable constraint can express non-emptiness for free text.

## Present in code, unasserted by the suite

- **REG-010 / REG-028** — no test anywhere uses an *undeclared* key in a declared namespace. Neither
  half of the asymmetry is asserted: that `agent.smuggled` is refused `UnknownKey` under a full grant,
  nor that `KeyAuthority`'s namespace loop prices a key the key map does not carry. The namespace test
  (`FrontmatterAuthorityTests.cs:45`) uses `agent.mode`, which *is* declared.
- **REG-017** — `DateConstraint` uses `DateTime.TryParseExact`, so it rejects a well-shaped but unreal
  date (`2026-13-45`) that the old format-only regex accepted. `ValueConstraintTests.cs:11-16` has no
  row for an impossible date, so the stricter behavior is untested.
- **REG-021 ordering** — the control-character check runs before the key's own value constraint
  (`FrontmatterWrite.cs:272-279`), so a control character in a constrained key reports as a control
  character rather than a shape failure. No test pins the ordering.
- **REG-051 second arm** — a block-list key whose value is `''` reports
  `InlineValueWhereBlockListDeclared` rather than passing. Untested, as it was in the old suite.

## Dismissed

- **REG-001** — the recognized set and its order were a hardcoded seven-key ordered table; the new set is whatever a vault declares, and key order in a note is the note's own — no counterpart.
- **REG-002** — "exactly these three keys are agentic" was a property of the table; authority is now per-key `write_requires` plus namespace rules the operator declares — no counterpart.
- **REG-003** — "non-agentic keys carry a pattern, agentic keys none" is now enforced by shape: the loader refuses a `block_list` key that declares a `value` and a `scalar` key that names an `entry` (`FrontmatterRegistryLoader.cs:130-133`), which is the same invariant by construction and additionally permits a constrained scalar key inside a protected namespace.
- **REG-005** — which two keys are nav block lists is the declaration's, not the code's — no counterpart.
- **REG-018** — whether `doc.type` is a character class or an enumeration is now the declaration's choice; both kinds exist (`CharClassConstraint`, `EnumerationConstraint`) — no counterpart.
- **REG-022** — "found a list" and "found a mapping" collapse into one `BlockWhereScalarDeclared` rule; the distinction was message granularity, and the corpus filter that depended on it (REG-031) has no counterpart either.
- **REG-030** — the corpus check parsed every note under `test-vault` and `C:\Repos\vault-primary`; this repository cannot reach a vault, by design — no counterpart.
- **REG-031** — same: no corpus to validate the declaration against.
- **REG-032** — same: the "exactly six unowned keys" diff is a claim about a corpus this build cannot see.
- **REG-033** — there is no exclusion list to hold `.claude/skills`; `Spec-Tool-Capabilities.md:84` fixes the rule in code as "nothing under a dot-directory", which covers that prefix and needs no declaration — no counterpart.
- **REG-038** — a path leaving the prefix via `..` is now excluded rather than admitted, because the whole dot directory is excluded; the old rule's purpose was to stop a prefix-string match over-matching, and the new rule has no prefix string.
- **REG-039** — `.claude/guides/*.md` and `.claude/settings.json` are now excluded; a deliberate widening fixed in `Spec-Tool-Capabilities.md:84`, at the cost the harvest names (those guides are vault notes carrying vault frontmatter, and nothing can write them now).
- **REG-040** — the dot rule applies to any segment at any depth, so a nested `Areas/Homelab/.claude/skills/x.md` is now excluded where the old root-anchored prefix left it alone; deliberate per the same spec line, and it fails closed.
- **REG-041** — an empty or absent path throws `ArgumentException` rather than answering "not excluded" (`FrontmatterWrite.cs:170`), because the path is mandatory and an absent one would read as permission on exactly the files the exclusion protects; stronger, and asserted by `ThePathIsMandatory`.
- **REG-045** — the entry schema no longer "ignores" `doc.*` keys; one validator checks every declared key against its declaration, which reports strictly more than the two-validator split did.
- **REG-050** — a valueless block-list key (`agent.instructions:` with nothing under it) reports `InlineValueWhereBlockListDeclared` rather than `empty_block`; the violation still fires and refuses on the caller-supplied path, only the rule name and its prose differ.
