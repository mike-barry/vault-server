namespace VaultShared.Frontmatter;

/// <summary>
/// The loaded declaration: which frontmatter keys the tooling recognizes, what their values must
/// look like, and what authority writing each requires. A closed set of records with no JSON in
/// them, so the rules run against this shape and the file's own shape exists only inside
/// <see cref="FrontmatterRegistryLoader"/>.
///
/// It is a positive allowlist and it fails closed: an unrecognized key is a refusal, never
/// something implicitly classified as safe. Two sections rather than one table, because a key entry
/// is addressed by exact name and a namespace rule is about keys that are <em>not</em> entries -
/// a map keyed by name alone has nowhere to put the second.
/// </summary>
/// <param name="Keys">Declared keys by name, matched case-insensitively - two spellings of one key
/// are one key, the same way the note itself would read them.</param>
/// <param name="Namespaces">Prefix rules, in declared order. A match can only add to the required
/// authority; failing to match is not evidence that a key needs none.</param>
/// <param name="EntrySchemas">Schemas by name. A block-list key holds the instance from here, so
/// two keys naming one schema hold one object and cannot drift.</param>
public sealed record FrontmatterRegistry(
    IReadOnlyDictionary<string, KeyDeclaration> Keys,
    IReadOnlyList<NamespaceRule> Namespaces,
    IReadOnlyDictionary<string, EntrySchema> EntrySchemas);

/// <summary>Whether a declared key's node is a plain value or a list of entries. What the
/// flat-scalar-against-a-block refusal tests against, and read from here by the block-list
/// operations, the validator, and any completeness scan alike - restating it anywhere is how the
/// three drift.</summary>
public enum KeyShape
{
    Scalar,
    BlockList,
}

/// <summary>
/// One recognized key. Four things kept separate because they are checked at different moments and
/// produce different refusals.
/// </summary>
/// <param name="Shape">Scalar or block list - a property of the node rather than of its text.</param>
/// <param name="Value">For a scalar key, what its text must satisfy. Null means the declaration
/// asserted nothing about the text. Always null for a block-list key.</param>
/// <param name="Entry">For a block-list key, the shape every member must have. Always null for a
/// scalar key.</param>
/// <param name="WriteRequires">The additional op class writing this key requires, in the same
/// vocabulary the grant model uses. Null means the base write class suffices - never that the key
/// is exempt from a namespace rule that also matches it, since the requirement is a union.</param>
public sealed record KeyDeclaration(KeyShape Shape, ValueConstraint? Value, EntrySchema? Entry, string? WriteRequires)
{
    /// <summary>An operator's rationale, carried through for a display surface. Read by nothing
    /// that decides anything.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// A protected namespace: every key whose name starts with <paramref name="Prefix"/> requires
/// <paramref name="WriteRequires"/>. Namespaces exist because the key map says nothing about keys
/// it does not list, and such keys do occur - an edit leaves an undeclared key already in a note
/// untouched, so it stays. Without this, changing one would require no authority at all.
/// </summary>
public sealed record NamespaceRule(string Prefix, string WriteRequires)
{
    /// <summary>An operator's rationale, carried through for a display surface.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The shape a block list's members must have: one or more variants, of which an entry must match
/// exactly one. What lets a single list hold both a bare scalar and several distinct mapping
/// shapes.
///
/// A schema does not declare where an entry goes. Ordering conventions belong to the tool composing
/// the write.
/// </summary>
public sealed record EntrySchema(IReadOnlyList<EntryVariant> Variants);

/// <summary>
/// One shape an entry may take.
/// </summary>
/// <param name="Name">What a caller and a refusal call this variant.</param>
/// <param name="DiscriminatedBy">The field whose presence selects this variant, for a mapping
/// variant. Null for a scalar one.</param>
/// <param name="Scalar">What a bare value must satisfy, for a scalar variant. Null for a mapping
/// one. Exactly one of this and <paramref name="DiscriminatedBy"/> is set - a selector is a
/// discriminating field or a scalar constraint, never both, which is what lets variants be matched
/// by walking them once.</param>
/// <param name="Fields">The variant's fields by name, each required or optional with its own value
/// constraint. Empty for a scalar variant.</param>
/// <param name="ExactlyOneOf">A set of fields of which exactly one must be present, checked in both
/// directions. Empty when the variant declares no such group.</param>
/// <param name="Identity">The field, or the exclusive group, whose value addresses the entry.
/// Everything that addresses an entry by target value resolves to this: duplicate detection,
/// idempotent adds, removals, and positional anchors. Empty when the variant has no identity.</param>
/// <param name="IdentityMatch">How that value compares. Declared per variant because the right
/// answer differs within a single list: an identity naming a file or a wikilink compares
/// case-insensitively, matching how the filesystem and the editor resolve it, while a line of human
/// prose compares exactly.</param>
/// <param name="RemovableWholesale">Whether a removal may name the variant with no target and clear
/// every matching entry. Right for tooling-owned entries, wrong for user-authored ones.</param>
public sealed record EntryVariant(
    string Name,
    string? DiscriminatedBy,
    ValueConstraint? Scalar,
    IReadOnlyDictionary<string, EntryField> Fields,
    IReadOnlyList<string> ExactlyOneOf,
    IReadOnlyList<string> Identity,
    StringComparison IdentityMatch,
    bool RemovableWholesale)
{
    /// <summary>An operator's rationale, carried through for a display surface.</summary>
    public string? Note { get; init; }
}

/// <summary>One field of a mapping variant.</summary>
/// <param name="Required">Whether an entry matching this variant must carry it. A field in an
/// exclusive group is declared optional; the group is what makes one of them required.</param>
/// <param name="Value">What the field's text must satisfy. Null means the declaration asserted
/// nothing about it.</param>
public sealed record EntryField(bool Required, ValueConstraint? Value)
{
    /// <summary>An operator's rationale, carried through for a display surface.</summary>
    public string? Note { get; init; }
}
