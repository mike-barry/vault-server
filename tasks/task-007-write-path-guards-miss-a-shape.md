# task-007: Close two guards in the frontmatter write path that miss a shape

**Goal:** A block-list key holding a mapping is refused with a coded refusal rather than throwing, and control-character hygiene reaches a caller-supplied block's entry values as it already reaches operation values.

## Context

Two findings from the full code review (plan step 1), both surviving an independent refutation pass. Both are the same kind of defect: a guard on the single write path is applied to one shape or one entry route and not to the other, so the path that admits the text refuses it and the path beside it does not.

**Shape — `src/VaultShared/Frontmatter/FrontmatterWrite.cs:507`.** `Add` refuses only `FrontmatterShape.Scalar`. `FrontmatterTree.ShapeOf` (`FrontmatterTree.cs:167-173`) returns `Block` for a `YamlMappingNode` as well as a sequence, which is what `docs/spec/Spec-Frontmatter-Substrate.md:Shape Detection` requires of it and what `FrontmatterTreeTests.cs:147` pins. So a declared block-list key whose note value is a mapping passes the scalar check, resolves zero entries, and reaches `FrontmatterTree.InsertEntry`, which throws `InvalidOperationException` at `FrontmatterTree.cs:255`.

`ToolRefusalEnvelope.Refusal` translates only `ToolRefusal`, `FrontmatterRefusal` and `AuthorityRefusal` (`ToolRefusalEnvelope.cs:70-76`), so this reaches both front-ends as an uncoded internal error rather than the `ShapeMismatch` the closed reason set promises. The trigger is specifically an anchorless `add_entry` — with an anchor it is already a coded `unresolved_identity`.

The removal side is the more serious half. `RequireClearableWholesale` (`FrontmatterWrite.cs:477-489`) resolves zero entries from a mapping, so its `Any` check passes vacuously and `tree.Remove` deletes the structure. A `remove_field` against such a key destroys a mapping no schema was ever consulted about, and reports success.

Nothing upstream refuses first: the validate phase (`FrontmatterWrite.cs:265`) is declaration-only and never inspects what the note holds, `Parse` accepts a nested mapping as a well-formed root mapping, and `FrontmatterChecks.CheckKey` runs after the apply loop as reported violations rather than as a guard. No test exercises a mapping-valued block-list key.

**Hygiene — `src/VaultShared/Frontmatter/FrontmatterWrite.cs:227`.** `SuppliedTree` screens `tree.Scalar(key)`, which returns null for a sequence node (`FrontmatterTree.cs:180`), and `FrontmatterChecks.CheckEntry` applies only the declared `ValueConstraint`. So control characters in a supplied block's entry field values, and in a bare scalar entry, are never screened. The identical value through `edit`'s `add_entry` is refused `ControlCharacter` at `FrontmatterWrite.cs:345`.

`docs/spec/Spec-Frontmatter-Substrate.md:What Throws And What Comes Back As Data` binds hygiene to "entry field values as well as scalar values", and gives the reason — terminal escapes are control characters, and the most agent-facing text in the vault lives in block-list entries. The supplied-block path is the one that admits the most caller text at once.

Exposure is registry-dependent, which is why this is second: a field declared `charclass`, `enum`, `date` or `wikilink` is incidentally protected by its own constraint, and only a `string`-constrained field or scalar variant is open. Against the fixture registry the refutation pass confirmed the round trip end to end — both a literal ESC byte and the YAML `\x1b` escape reach the value as a real 0x1B, the emitter writes it back as `\e`, and a re-read hands back the real control byte. No test covers the supplied-block case.

## Acceptance criteria

- [ ] The block-list operations test for a sequence rather than for "not scalar". A mapping-valued declared block-list key is refused `ShapeMismatch` in `Add`, `Remove`, and `RequireClearableWholesale`, before any entry resolution.
- [ ] `FrontmatterTree.ShapeOf` keeps returning `Block` for a mapping — that is the spec's three-answer shape query and is not what changes. The new distinction is a separate query on the tree.
- [ ] `SuppliedTree` screens the supplied tree's values exhaustively: for a sequence-valued key, every entry's scalar value and every field value of every mapping entry, refusing `ControlCharacter` naming the key.
- [ ] A test drives `add_entry` and `remove_field` against a note whose declared block-list key holds a mapping, asserting a coded refusal in both directions and that the structure survives the refused removal.
- [ ] A test drives `create` with a supplied block carrying a control character in a `string`-constrained entry field and in a bare scalar entry, asserting `ControlCharacter` in both cases. Hygiene refuses rather than strips, so assert the refusal and not a rewritten value.
- [ ] Any text in `docs/design/Design-Frontmatter-Surface.md` enumerating the refusals either path raises is rewritten in the same commit, or confirmed to already describe the new behaviour.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

The hygiene finding as first reported used `use_skill: "run\x1b[31m"` as its example. That value is refused already — `use_skill` is declared `charclass alnum-dash` in the fixture registry, so it fails `ValueRefused` on the whole-block path. Use a `string`-constrained field (`rule`, `when`, `description`, `load_when`) or the `hint` scalar variant when writing the test, or it will pass for the wrong reason.
