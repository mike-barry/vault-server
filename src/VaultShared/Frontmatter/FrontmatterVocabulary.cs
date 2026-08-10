namespace VaultShared.Frontmatter;

/// <summary>
/// Describes what a frontmatter operation may carry, for a client to read. It is a projection of
/// the loaded declaration, with one branch per entry variant, generated from the same
/// <see cref="FrontmatterRegistry"/> instance the write path holds, so there is no second copy to
/// drift.
///
/// This type advertises and does not enforce. The write path is the sole enforcement point, and a
/// payload satisfying everything advertised here can still be refused there. Constraints with no
/// clean client-facing expression -
/// an exclusive group, an identity, whether a variant clears wholesale - are carried in
/// <see cref="EntryBranch.Description"/> rather than dropped or reshaped into something a client
/// would read as enforceable.
///
/// The shape is a neutral model rather than a rendered schema document. Which schema dialect and
/// envelope carry it to a client belongs to the tool vocabulary, because this layer must not know
/// which front-end is asking.
/// </summary>
/// <param name="Keys">Every declared key, so a client can name one. No front-end projects this. The
/// advertised <c>edit</c> schema is built from <paramref name="Branches"/> alone, and a refusal that
/// names the declared keys reads them from the declaration itself, because the write path is the
/// enforcement point and must not depend on an advertising projection.</param>
/// <param name="Branches">One branch per entry variant across every block-list key. Empty where no
/// declaration is loaded, so a failed load reaches a client as <c>edit</c> still advertised with its
/// ordinary argument surface and its variant branches simply absent.</param>
public sealed record FrontmatterVocabulary(
    IReadOnlyList<KeyAdvertisement> Keys,
    IReadOnlyList<EntryBranch> Branches)
{
    /// <summary>Gets the projection a vault with no usable declaration advertises, holding no keys
    /// and no entry branches.</summary>
    public static FrontmatterVocabulary None { get; } = new([], []);

    /// <summary>Returns the projection of a vault's declaration state.</summary>
    /// <param name="state">The vault's declaration state.</param>
    /// <returns>The projection, or <see cref="None"/> where the declaration did not load.
    /// Generating a projection is a read, so a failed load answers rather than refusing.</returns>
    public static FrontmatterVocabulary Of(RegistryState state) =>
        state is RegistryState.Loaded loaded ? Of(loaded.Registry) : None;

    /// <summary>Returns the projection of a loaded declaration.</summary>
    /// <param name="registry">The declaration to project.</param>
    /// <returns>The advertised keys and entry branches.</returns>
    public static FrontmatterVocabulary Of(FrontmatterRegistry registry)
    {
        var keys = new List<KeyAdvertisement>();
        var branches = new List<EntryBranch>();

        foreach (var (name, declaration) in registry.Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            keys.Add(new KeyAdvertisement(
                name,
                declaration.Shape,
                declaration.Shape == KeyShape.Scalar ? Value(declaration.Value) : null,
                declaration.Note));

            if (declaration.Entry is not { } schema)
                continue;

            foreach (var variant in schema.Variants)
            {
                var existing = branches.FindIndex(b => ReferenceEquals(b.Schema, schema) && b.Variant == variant.Name);
                if (existing >= 0)
                {
                    // Two keys naming one schema hold one object, so this is one branch both keys
                    // accept rather than two identical ones a client has to reconcile.
                    branches[existing] = branches[existing] with { Keys = [.. branches[existing].Keys, name] };
                    continue;
                }

                branches.Add(Branch(schema, variant, name));
            }
        }

        return new FrontmatterVocabulary(keys, branches);
    }

    private static EntryBranch Branch(EntrySchema schema, EntryVariant variant, string key) =>
        new(
            schema,
            variant.Name,
            [key],
            variant.DiscriminatedBy,
            [
                .. variant.Fields.Select(f => new SchemaField(
                    f.Key,
                    // A branch is selected by the discriminating field's presence, so a client that
                    // sets it has chosen this variant and every other requirement of it.
                    f.Value.Required || string.Equals(f.Key, variant.DiscriminatedBy, StringComparison.OrdinalIgnoreCase),
                    Value(f.Value.Value),
                    f.Value.Note))
            ],
            variant.Scalar is null ? null : Value(variant.Scalar),
            Description(variant));

    /// <summary>
    /// Returns what the branch must say in prose because a client-facing schema cannot express it:
    /// the exclusive group, which field addresses the entry and how it compares, and whether the
    /// variant may be cleared wholesale. Each of these is enforced at the write path whatever a
    /// client makes of the sentence.
    /// </summary>
    /// <param name="variant">The variant to describe.</param>
    /// <returns>The description, empty where the variant has nothing extra to say.</returns>
    private static string Description(EntryVariant variant)
    {
        var parts = new List<string>();
        if (variant.DiscriminatedBy is { } discriminator)
            parts.Add($"An entry carrying \"{discriminator}\" is this variant.");
        if (variant.ExactlyOneOf.Count > 0)
            parts.Add($"Exactly one of {string.Join(", ", variant.ExactlyOneOf)} must be present.");
        if (variant.Identity.Count > 0)
            parts.Add(
                $"Addressed by {string.Join(" or ", variant.Identity)}, compared "
                + (variant.IdentityMatch == StringComparison.OrdinalIgnoreCase ? "case-insensitively." : "exactly."));
        if (variant.RemovableWholesale)
            parts.Add("Every entry of this variant may be cleared at once.");
        if (variant.Note is { Length: > 0 } note)
            parts.Add(note);

        return string.Join(" ", parts);
    }

    private static SchemaValue Value(ValueConstraint? constraint) => constraint switch
    {
        EnumerationConstraint enumeration => new SchemaValue("string", enumeration.Values, "One of the listed values."),
        DateConstraint date => new SchemaValue("string", null, $"A date written as {date.Format}."),
        CharClassConstraint charClass => new SchemaValue(
            "string", null, $"{charClass.Min} to {charClass.Max} characters from {charClass.Class.Spelling()}."),
        WikilinkConstraint => new SchemaValue("string", null, "A wikilink in bracket form, as [[Target]]."),
        _ => new SchemaValue("string", null, null),
    };
}

/// <summary>Represents one declared key, as a client sees it.</summary>
/// <param name="Key">The key's name.</param>
/// <param name="Shape">Whether the key holds a plain value or a list of entries.</param>
/// <param name="Value">What a scalar key's text must satisfy, and null for a block-list key, whose
/// entry branches carry the constraints.</param>
/// <param name="Description">The operator's own note, where the declaration carries one.</param>
public sealed record KeyAdvertisement(string Key, KeyShape Shape, SchemaValue? Value, string? Description);

/// <summary>
/// Represents one shape an entry may take, as one branch of the advertised alternation.
/// </summary>
/// <param name="Schema">The declared schema this branch belongs to. It is identified by reference
/// equality, since two keys naming one schema hold one instance.</param>
/// <param name="Variant">The variant's name.</param>
/// <param name="Keys">The declared keys that accept it.</param>
/// <param name="Discriminator">The field whose presence selects this branch, and null for a scalar
/// branch.</param>
/// <param name="Fields">The variant's fields. Empty for a scalar branch.</param>
/// <param name="Scalar">What a bare value must satisfy, for a scalar branch, and null for a mapping
/// one.</param>
/// <param name="Description">What no client-facing schema expresses cleanly.</param>
public sealed record EntryBranch(
    EntrySchema Schema,
    string Variant,
    IReadOnlyList<string> Keys,
    string? Discriminator,
    IReadOnlyList<SchemaField> Fields,
    SchemaValue? Scalar,
    string Description);

/// <summary>Represents one field of a branch.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Required">Whether an entry on this branch must carry it.</param>
/// <param name="Value">What the field's text must satisfy.</param>
/// <param name="Description">The operator's own note, where the declaration carries one.</param>
public sealed record SchemaField(string Name, bool Required, SchemaValue Value, string? Description);

/// <summary>Represents a value's advertised shape.</summary>
/// <param name="Type">The JSON type name, always <c>string</c>. The parse leaves every scalar as the
/// text that was written, so a value that is a date or a number in a client's eyes is a string in
/// the note.</param>
/// <param name="Enum">The permitted values, where the constraint is an enumeration, and null
/// otherwise.</param>
/// <param name="Description">What the constraint wanted, in the words a violation would use.</param>
public sealed record SchemaValue(string Type, IReadOnlyList<string>? Enum, string? Description);
