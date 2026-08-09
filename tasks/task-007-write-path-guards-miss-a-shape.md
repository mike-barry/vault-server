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

- [x] The block-list operations test for a sequence rather than for "not scalar". A mapping-valued declared block-list key is refused `ShapeMismatch` in `Add`, `Remove`, and `RequireClearableWholesale`, before any entry resolution.
- [x] `FrontmatterTree.ShapeOf` keeps returning `Block` for a mapping — that is the spec's three-answer shape query and is not what changes. The new distinction is a separate query on the tree.
- [x] `SuppliedTree` screens the supplied tree's values exhaustively: for a sequence-valued key, every entry's scalar value and every field value of every mapping entry, refusing `ControlCharacter` naming the key.
- [x] A test drives `add_entry` and `remove_field` against a note whose declared block-list key holds a mapping, asserting a coded refusal in both directions and that the structure survives the refused removal.
- [x] A test drives `create` with a supplied block carrying a control character in a `string`-constrained entry field and in a bare scalar entry, asserting `ControlCharacter` in both cases. Hygiene refuses rather than strips, so assert the refusal and not a rewritten value.
- [x] Any text in `docs/design/Design-Frontmatter-Surface.md` enumerating the refusals either path raises is rewritten in the same commit, or confirmed to already describe the new behaviour.
- [x] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

The hygiene finding as first reported used `use_skill: "run\x1b[31m"` as its example. That value is refused already — `use_skill` is declared `charclass alnum-dash` in the fixture registry, so it fails `ValueRefused` on the whole-block path. Use a `string`-constrained field (`rule`, `when`, `description`, `load_when`) or the `hint` scalar variant when writing the test, or it will pass for the wrong reason.

### What was done

The new query is `FrontmatterTree.HoldsBlockList` (`FrontmatterTree.cs:175`), public beside `ShapeOf`, answering true only for a `YamlSequenceNode`. `ShapeOf` is untouched, so `FrontmatterTreeTests.cs:147` still pins `Block` for a mapping.

Three sites now ask it, each after the existing scalar check so the scalar case keeps its own message:

- `Add` (`FrontmatterWrite.cs:521`) — refuses a present-but-not-a-sequence key. The `Absent` case still falls through, because an add creates its key.
- `Remove` (`FrontmatterWrite.cs:580`) — refuses after the `Absent` early return, so a removal against an absent key is still a no-op.
- `RequireClearableWholesale` (`FrontmatterWrite.cs:492`) — refuses after the `declaration.Entry is null` check and before `EntryResolver.Entries`. Order matters: a key declared *scalar* that holds a block keeps its existing `WholesaleRemovalRefused`, which a declared block-list key never reaches.

Hygiene: `SuppliedTree`'s inline scalar check became `RefuseControlCharacters(FrontmatterTree, string)` (`FrontmatterWrite.cs:255`), an overload beside the per-operation one. It walks `tree.Scalar(key)`, then every entry from `tree.Entries(key)` — the bare scalar's `Value` and every `Field` of a mapping entry. `Entries` returns empty for a non-sequence, so a nested mapping under a key contributes nothing; that is the criterion's extent, and a control character in a nested mapping's value is still unscreened. The refusal message is unchanged, so nothing pinned to it moves.

### Dead ends and things checked

- Field *names* in a supplied block's entries are not screened. The criterion says "every field value"; a name carrying an escape still refuses, as `UnknownField` → `ValueRefused`, so it is refused with a less accurate code rather than admitted.
- `ControlCharacter` and `ValueRefused` both translate to `ToolCodes.InvalidValue` (`ToolRefusalEnvelope.cs:104-105`), so a tool-level test cannot tell the hygiene refusal from a constraint refusal. The hygiene test therefore sits at the write-path level in `FrontmatterRefusalTests.cs` and asserts `RefusalReason.ControlCharacter` directly, using the supplied-block call shape that `create` runs. `ShapeMismatch` has its own wire code, so the shape test sits in `EditToolTests.cs` where the file on disk shows the mapping surviving the refused removal.
- The escape is written into the supplied block as YAML's own `\x1b` inside a double-quoted scalar rather than as a raw control byte emitted by a C# escape, so the block's text is what a caller would actually send.
- `docs/design/Design-Frontmatter-Surface.md` was read and not edited. Its refusal enumeration (line 189) already names "a shape mismatch" and "a control character in caller text", and its phase list (line 146) already carries hygiene and shape in the validate phase. `Spec-Frontmatter-Substrate.md` already binds hygiene to "entry field values as well as scalar values" and already lists shape mismatch, so neither document describes behaviour this change contradicts. The design's "Shape is a query with three answers" sentence stays true and the new query is not mentioned there.
