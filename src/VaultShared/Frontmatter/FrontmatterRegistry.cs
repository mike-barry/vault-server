namespace VaultShared.Frontmatter;

/// <summary>
/// Represents a vault's loaded frontmatter declaration: which keys the tooling recognizes, what
/// their values must look like, and what authority writing each requires. These records hold no
/// JSON, so the rules run against this shape and the file's own shape stays inside
/// <see cref="FrontmatterRegistryLoader"/>.
///
/// The declaration is a positive allowlist that fails closed: an unrecognized key is refused, never
/// treated as safe by default. Keys and namespaces are separate sections because a key is addressed
/// by exact name while a namespace rule is about keys that are not listed at all.
/// </summary>
/// <param name="Keys">Declared keys by name, matched case-insensitively, so two spellings of one
/// key are one key just as the note itself would read them.</param>
/// <param name="Namespaces">Prefix rules, in declared order. A match can only add to the required
/// authority; failing to match is not evidence that a key needs none.</param>
/// <param name="EntrySchemas">Schemas by name. A block-list key holds the instance from here, so
/// two keys naming one schema hold one object and cannot drift.</param>
public sealed record FrontmatterRegistry(
    IReadOnlyDictionary<string, KeyDeclaration> Keys,
    IReadOnlyList<NamespaceRule> Namespaces,
    IReadOnlyDictionary<string, EntrySchema> EntrySchemas);

/// <summary>Specifies whether a declared key holds a plain value or a list of entries. The
/// shape-mismatch refusal tests against this, and the block-list operations, the validator, and any
/// completeness scan all read it from here rather than restating it.</summary>
public enum KeyShape
{
    /// <summary>The key holds a single plain value.</summary>
    Scalar,

    /// <summary>The key holds a list of entries.</summary>
    BlockList,
}

/// <summary>
/// Represents one recognized key. The four parts are kept separate because they are checked at
/// different moments and produce different refusals.
/// </summary>
/// <param name="Shape">Scalar or block list. A property of the node rather than of its text.</param>
/// <param name="Value">For a scalar key, what its text must satisfy. Null means the declaration
/// asserted nothing about the text. Always null for a block-list key.</param>
/// <param name="Entry">For a block-list key, the shape every member must have. Always null for a
/// scalar key.</param>
/// <param name="WriteRequires">The additional op class writing this key requires, in the same
/// vocabulary the grant model uses. Null means the base write class suffices. It never means the
/// key is exempt from a namespace rule that also matches it, because the requirement is a
/// union.</param>
public sealed record KeyDeclaration(KeyShape Shape, ValueConstraint? Value, EntrySchema? Entry, string? WriteRequires)
{
    /// <summary>Gets the operator's own note, carried through for display. Nothing branches on
    /// it.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Represents a protected namespace: every key whose name starts with <paramref name="Prefix"/>
/// requires <paramref name="WriteRequires"/>. Namespaces exist because the key map says nothing
/// about keys it does not list, and such keys do occur, since an edit leaves an undeclared key
/// already in a note untouched. Without this rule, changing one would require no authority at all.
/// </summary>
/// <param name="Prefix">The key-name prefix the rule matches, case-insensitively.</param>
/// <param name="WriteRequires">The op class writing a matching key requires.</param>
public sealed record NamespaceRule(string Prefix, string WriteRequires)
{
    /// <summary>Gets the operator's own note, carried through for display.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Represents the shape a block list's members must have: one or more variants, of which an entry
/// must match exactly one. This is what lets a single list hold both a bare scalar and several
/// distinct mapping shapes.
///
/// A schema does not declare where an entry goes. Ordering conventions belong to the tool composing
/// the write.
/// </summary>
/// <param name="Variants">The shapes an entry may take.</param>
public sealed record EntrySchema(IReadOnlyList<EntryVariant> Variants);

/// <summary>
/// Represents one shape an entry may take.
/// </summary>
/// <param name="Name">What a caller and a refusal call this variant.</param>
/// <param name="DiscriminatedBy">The field whose presence selects this variant, for a mapping
/// variant. Null for a scalar one.</param>
/// <param name="Scalar">What a bare value must satisfy, for a scalar variant. Null for a mapping
/// one. Exactly one of this and <paramref name="DiscriminatedBy"/> is set, which is what lets
/// variants be matched by walking them once.</param>
/// <param name="Fields">The variant's fields by name, each required or optional with its own value
/// constraint. Empty for a scalar variant.</param>
/// <param name="ExactlyOneOf">A set of fields of which exactly one must be present, checked in both
/// directions. Empty when the variant declares no such group.</param>
/// <param name="Identity">The field, or the exclusive group, whose value addresses the entry.
/// Duplicate detection, idempotent adds, removals, and positional anchors all resolve to this.
/// Empty when the variant has no identity.</param>
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
    /// <summary>Gets the operator's own note, carried through for display.</summary>
    public string? Note { get; init; }
}

/// <summary>Represents one field of a mapping variant.</summary>
/// <param name="Required">Whether an entry matching this variant must carry it. A field in an
/// exclusive group is declared optional; the group is what makes one of them required.</param>
/// <param name="Value">What the field's text must satisfy. Null means the declaration asserted
/// nothing about it.</param>
public sealed record EntryField(bool Required, ValueConstraint? Value)
{
    /// <summary>Gets the operator's own note, carried through for display.</summary>
    public string? Note { get; init; }
}
