# Spec Frontmatter Registry

Forward specification for the declaration the tooling is authorized by: which frontmatter keys it recognizes, what their values must look like, and what authority writing each requires. Its companion, `Spec-Frontmatter-Substrate.md`, owns how a block is parsed, mutated, and emitted. The two meet at the single write path, which runs this layer's whole-block validation and its per-operation validation.

The declaration is one file, and each consumer takes the sections that bind it: the key, namespace, and entry-schema sections bind every frontmatter writer, and a future non-markdown surface takes its own. One file because every key-level protection derives from it and they share a trust tier, a lifecycle, and a load cascade.

## The Declaration

**One declaration of recognized keys, and it is data rather than code.** Extending the vocabulary - a new `agent.warning`, a new `tasks.*` family - is an edit to one file. What the layer implements is the mechanism: how a declaration is expressed, validated, and enforced. Which keys a vault declares is not this spec's subject.

**It is a positive allowlist and it fails closed.** An unrecognized key is a refusal, never something implicitly classified as safe. Write authority follows from declared membership, never from a pattern match against a key's name.

An allowlist avoids a circular dependency. A scheme that decided protection by inspecting a key's name would have to parse the key before it could classify it, so anything the parse got wrong would come out unprotected - the check would depend on the thing it is checking. An allowlist refuses an undeclared key before its shape matters at all.

## Where The Declaration Lives

**The file lives in the server repository, outside the vault, named by the vault's own configuration entry.** It is never discovered by walking vault content. Two vaults may name one file and share a vocabulary, or name their own.

This is a trust boundary. Every protection in this layer derives from the registry, so a caller able to write the file could declare an agentic key unprotected and rewrite the agentic layer without holding the elevated capability. Path exclusion does not cover this: exclusion refuses frontmatter operations, and a body write to a configuration file is not one. The file belongs beside the grant and op-class configuration, at the same trust tier.

**The runtime reads the declaration and never writes it.** No operation on either front-end edits it; edits are made by an operator with an editor. That is what keeps the agent-facing surface from redefining what counts as protected.

**The format is JSON, with comments and trailing commas tolerated on read.** Comments serve the operator and are not preserved through a read, which the read-only rule makes moot. Where a rationale must reach a display surface, a declaration entry may carry an explicit note field.

**The declaration names a `version`, and this layer reads version 1.** The field may be omitted; where it is present it must be the JSON number 1, and anything else - a later number, or the string `"1"` - fails the load. A declaration written against a later shape is therefore not read as this one, which a check on unrecognized top-level keys alone would not catch, since a later shape may repurpose a key this one already has.

## What A Key Declaration Expresses

Four things, kept separate because they are checked at different moments and produce different refusals:

- **Shape** - scalar or block list. What the flat-scalar-against-a-block refusal tests against, and a property of the node rather than of its text.
- **Value constraint** - for scalar keys, what the text must satisfy.
- **Entry schema** - for block-list keys, the shape every member must have. Declared separately and referenced by name, so two keys holding the same kind of entry are identical by construction.
- **Write authority** - the additional op class, if any, that writing the key requires. Absent means the base write class suffices.

**Which keys are block lists is read from the declaration, never restated.** The block-list operations, the validator, and any completeness scan derive it from the same source or they drift.

## Entry Schemas

An entry schema declares **one or more variants**; an entry is valid if it matches exactly one. This is what lets a single list hold both a bare scalar and several distinct mapping shapes.

A variant declares:

- **Its selector.** A **scalar** variant is a plain value satisfying a value constraint, with no fields. Otherwise the variant is a mapping selected by **the presence of a named discriminating field**. Two variants may not share a discriminating field, and a mapping matching none of them is a refusal.
- **Its fields**, each required or optional, each with a value constraint.
- **Mutually exclusive groups** - a set of fields of which exactly one must be present, checked in both directions.
- **Its identity** - the field, or the exclusive group, whose value addresses the entry. Everything in `Spec-Frontmatter-Substrate.md` that addresses an entry by target value resolves to this: duplicate detection, idempotent adds, removals, and positional anchors.
- **How its identity compares** - case-sensitively or not, declared per variant because the right answer differs within a single list. An identity that names a file or a wikilink compares case-insensitively, matching how the filesystem and the editor resolve it; otherwise a scan and an operation disagree about whether an entry is already there. An identity that is a line of human prose compares exactly.
- **Whether the variant is removable wholesale** - whether a removal may name the variant with no target and clear every matching entry. Right for tooling-owned entries, wrong for user-authored ones.

**An entry schema does not declare where an entry goes.** Ordering conventions belong to the tool composing the write. This layer appends absent an anchor and honors one when given.

## Write Authority

**Authority is named as an op class**, in the same vocabulary the grant model uses - so there is no second enumeration of protection levels to keep in step, and nothing to translate between them.

**Requirements add up.** What a write requires is the union of what every key it changes declares, plus anything a matching namespace adds. Because it is a union, a namespace can only raise the requirement, never lower one already there - that falls out of the operation rather than being a rule anyone has to apply.

**A new protection class is data** - a line in this file and a line in the op-class configuration.

**Whole-block validation asserts the required authority on a newly supplied tree before it is serialized**, ahead of and independent of the outcome diff in `Spec-Frontmatter-Substrate.md`. Not redundant: the outcome diff remains the authority on what actually changed, while this is the registry's statement that a key requiring elevation was supplied at all.

## Protected Namespaces

The declaration carries a second section: namespace prefixes, each naming the authority that keys under it require.

Namespaces exist because the key map says nothing about keys it does not list, and such keys do occur: an edit leaves an undeclared key already in a note untouched, so it stays. Without a namespace rule, changing one of those would require no authority at all, and a write to the agentic layer would come out needing nothing.

**A namespace match can only add to the required authority.** Nothing is classified as needing no authority by *failing* to match: a negative match is not evidence, and treating it as evidence reopens the loop above.

This is why the declaration is two sections rather than one table. A key entry is addressed by exact name; a namespace rule is about keys that are not entries. An implementation modelling the registry as a map keyed by name alone has nowhere to put the second rule.

## The Value Constraint Vocabulary

**Constraints are named kinds with parameters, from a closed set**: a date format, an enumeration, a character class with length bounds, a wikilink in its required bracket form, and an unconstrained string. The same vocabulary serves scalar keys and entry fields. A block-list key takes no value constraint; its entry schema carries them.

**There is no raw regular expression kind.** The declaration is external input, and an uncompiled pattern from a file is both a denial-of-service surface and a source of violation messages no caller can act on - a named kind reports what it wanted, a regex only that it did not match. A constraint needing one later is added as another named kind, compiled and time-bounded at load.

**Dates are format checks on text.** The parse must leave date-shaped scalars as strings rather than coercing them, or the format check tests something the caller never wrote and the round trip can rewrite their text.

**A date constraint naming no format takes `yyyy-MM-dd`**, matched whole under the invariant culture. That is the ISO 8601 calendar date, which is what the editor writes and what sorts correctly as text. A format written empty is a load failure rather than a way of asking for the default.

**A constraint may be a deliberate placeholder.** The document type is the standing example: the vault has no authoritative table of types, its conventions doc defers to a table that does not exist, and the values in use mix established types with one-offs. A guessed enumeration would break the automated remedy that sets missing types on exactly the notes it exists to fix. A character class with length bounds stops the field carrying smuggled text meanwhile, and replacing it with an enumeration is an edit to the file rather than a change to this layer.

## Two Extents, One Closed Perimeter

**Creating a note validates the whole supplied block** - the caller supplies every key, so all of it is checked.

**Editing validates only the keys an operation touches**, passing an undeclared key already present in the note through untouched.

The asymmetry is load-bearing. Every path by which a key can *enter* the vault is closed: creation admits only declared keys and editing can only write declared keys, so an undeclared key never appears in a note that did not already have one. Whole-block validation on the edit path would instead make every note already carrying an undeclared key unwritable until that key was stripped.

**Values are enforced, not just names.** A declared key must satisfy its constraint on the edit path exactly as on the creation path, or a caller blocked from inventing a key could put arbitrary text into a legitimate one.

**The checks compiled into the layer follow the extent rather than being enforced alike on both.** A supplied block refuses on every one of them, because the caller wrote every key and can fix what it sent; on the edit path they come back as violations in the outcome, since a key an edit touches may already have carried the problem (`Spec-Frontmatter-Substrate.md:What Throws And What Comes Back As Data`). One consequence is reachable: setting a scalar key whose declaration constrains nothing to an empty value writes it and reports a violation, where the same key left empty in a supplied block is refused. A constraint with a floor of its own - a date, an enumeration, a wikilink, a character class requiring at least one character - refuses the empty value on both paths.

**Removal binds to the declaration too.** A removal *is* a write; without binding it a caller could delete a key it had no authority to create.

## Loading And Failure

**The declaration loads once, at startup, into the context the tools receive.** Both front-ends load the same file through the same path; if one loaded at startup and the other re-read per invocation, they would disagree about the vocabulary the moment the file was edited mid-session.

**There is no hot reload.** A batch of operations spanning one would be validated against two registries.

**The declaration is validated at load, not at first use.** What must be caught there: a `version` that is present and is not the number 1, an unknown constraint kind, a character class whose bounds cross, an enumeration with no members, a shape and constraint that contradict each other, a block-list key naming a schema that does not exist, a schema whose identity or exclusive group names a field the variant does not declare, two variants of one schema sharing a discriminating field, and an op class the op-class configuration does not define. That last is a cross-file check, precedented by grants already validated against the op-class registry at load, so the registry joins the same load cascade and degradation behavior.

**A key declaring no additional authority, whose name falls under a namespace that does, requires the namespace's authority.** The union produces that outcome regardless; it is stated so a key entry is not read as an override.

**A missing or unparseable declaration fails closed on every frontmatter write and stays open on everything else.** Without it nothing can tell whether a supplied key is declared, so a write cannot be validated. Reads are unaffected, and so are removal and relocation: what prices those is path policy, which is declared elsewhere. There is no fallback to a built-in default set; a silent fallback is how a failed load becomes a vault with nothing protected. The failure surfaces through the host's health state rather than by crashing, so the condition is visible rather than inferred from refusals.

**Advertisement does not change with the failure.** `edit` stays listed with its ordinary argument surface; the schema's variant branches are generated from the declaration and are simply absent without it. Frontmatter operations refuse at the write path, where every other refusal already lives. Advertising was never permission (`Spec-Frontmatter-Substrate.md:How The Vocabulary Reaches Callers`).

## An Illustrative Declaration

Shape only. The keys and namespaces below are a vault's data, not part of this specification.

```jsonc
{
  "version": 1,

  "namespaces": [
    { "prefix": "agent.", "write_requires": "agentic:write" },
    { "prefix": "nav.",   "write_requires": "agentic:write" }
  ],

  "keys": {
    "doc.created":  { "shape": "scalar", "value": { "kind": "date" } },
    "doc.archived": { "shape": "scalar", "value": { "kind": "date" } },

    // Character class rather than an enumeration: no authoritative type table exists yet.
    "doc.type": {
      "shape": "scalar",
      "value": { "kind": "charclass", "chars": "alpha-dash", "min": 1, "max": 20 }
    },

    // Both navigation lists share one entry schema, so they cannot drift apart.
    "nav.contents": { "shape": "block_list", "entry": "nav_entry" },
    "nav.see_also": { "shape": "block_list", "entry": "nav_entry" },

    "agent.instructions": { "shape": "block_list", "entry": "instruction_entry" }
  },

  "entry_schemas": {

    "nav_entry": {
      "variants": [
        {
          "name": "entry",
          "fields": {
            "key":         { "required": true,  "value": { "kind": "string" } },
            "description": { "required": true,  "value": { "kind": "string" } },
            "link":        { "required": false, "value": { "kind": "wikilink" } },
            "folder":      { "required": false, "value": { "kind": "string" } },
            "load_when":   { "required": false, "value": { "kind": "string" } }
          },
          "exactly_one_of": ["link", "folder"],
          "identity": ["link", "folder"],
          "identity_match": "case-insensitive"
        }
      ]
    },

    "instruction_entry": {
      "variants": [
        // A bare string is a revisable hint - no discriminator, no identity.
        { "name": "hint", "scalar": { "kind": "string" } },

        // The user's own binding rule: matched exactly, removed one at a time.
        {
          "name": "pinned",
          "discriminated_by": "pinned",
          "fields": {
            "pinned": { "required": true, "value": { "kind": "string" } }
          },
          "identity": ["pinned"],
          "identity_match": "case-sensitive"
        },

        // Tooling-owned routing: clearable wholesale, with no target named.
        {
          "name": "use_skill",
          "discriminated_by": "use_skill",
          "fields": {
            "use_skill": { "required": true,  "value": { "kind": "string" } },
            "when":      { "required": true,  "value": { "kind": "string" } },
            "note":      { "required": false, "value": { "kind": "string" } }
          },
          "identity": ["use_skill"],
          "identity_match": "case-insensitive",
          "removable_wholesale": true
        }
      ]
    }
  }
}
```

The agentic keys declare no authority of their own because their namespace supplies it; a key names `write_requires` only when it needs a class its namespace does not provide. The entry field named `key` is the vault's word for an entry's label, unrelated to a declared registry key - the specs use **declared key** and **entry field** to keep the two apart.
