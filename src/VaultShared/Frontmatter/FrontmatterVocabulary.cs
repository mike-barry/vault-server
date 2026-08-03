namespace VaultShared.Frontmatter;

/// <summary>
/// The machine-readable advertisement of what a frontmatter operation may carry: a projection of the
/// loaded declaration, one branch per entry variant, generated from the same
/// <see cref="FrontmatterRegistry"/> instance the write path holds. There is no second copy to drift
/// and so no drift test to write.
///
/// <b>It advertises; it does not enforce.</b> Generation is a read of the registry and asserts
/// nothing, which is what keeps the write path the sole enforcement point: a payload satisfying
/// everything advertised here can still be refused there. Constraints with no clean client-facing
/// expression - a mutually exclusive group, an identity, whether a variant clears wholesale - are
/// carried in <see cref="EntryBranch.Description"/> rather than dropped or invented into a shape a
/// client would read as enforceable.
///
/// The shape is a neutral model rather than a rendered schema document. What carries it to a client
/// - which schema dialect, which envelope - belongs to the tool vocabulary, and a projection that
/// chose one here would put that decision in the layer that must not know which front-end is asking.
/// </summary>
/// <param name="Keys">Every declared key, so a client can name one.</param>
/// <param name="Branches">One per entry variant across every block-list key. Empty where no
/// declaration is loaded, which is exactly how a failed load reaches a client: <c>edit</c> stays
/// advertised with its ordinary argument surface and its variant branches simply absent, because
/// advertising was never permission.</param>
public sealed record FrontmatterVocabulary(
    IReadOnlyList<KeyAdvertisement> Keys,
    IReadOnlyList<EntryBranch> Branches)
{
    /// <summary>What a vault with no usable declaration advertises: the operations, with nothing to
    /// say about keys or entries.</summary>
    public static FrontmatterVocabulary None { get; } = new([], []);

    /// <summary>
    /// The projection of a vault's declaration state. A state that did not load answers
    /// <see cref="None"/> rather than refusing: generation is a read, and refusing to advertise is
    /// not what a failed load does.
    /// </summary>
    public static FrontmatterVocabulary Of(RegistryState state) =>
        state is RegistryState.Loaded loaded ? Of(loaded.Registry) : None;

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
                    // Two keys naming one schema hold one object, so the branch is one branch that
                    // both keys accept rather than two identical ones a client has to reconcile.
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
                    // The discriminating field is pinned: a branch is selected by its presence, so a
                    // client that sets it has chosen this variant and every other requirement of it.
                    f.Value.Required || string.Equals(f.Key, variant.DiscriminatedBy, StringComparison.OrdinalIgnoreCase),
                    Value(f.Value.Value),
                    f.Value.Note))
            ],
            variant.Scalar is null ? null : Value(variant.Scalar),
            Description(variant));

    /// <summary>
    /// What the branch says in prose because a client-facing schema cannot say it: the exclusive
    /// group, which field addresses the entry and how it compares, and whether the variant may be
    /// cleared wholesale. Each of these is enforced at the write path whatever a client makes of the
    /// sentence.
    /// </summary>
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

/// <summary>One declared key, as a client sees it.</summary>
/// <param name="Value">What a scalar key's text must satisfy, and null for a block-list key - its
/// entry branches carry the constraints.</param>
/// <param name="Description">The operator's own note, where the declaration carries one.</param>
public sealed record KeyAdvertisement(string Key, KeyShape Shape, SchemaValue? Value, string? Description);

/// <summary>
/// One shape an entry may take, as one branch of the advertised alternation.
/// </summary>
/// <param name="Schema">The declared schema this branch belongs to. Reference equality is what
/// identifies it, since two keys naming one schema hold one instance.</param>
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

/// <summary>One field of a branch.</summary>
public sealed record SchemaField(string Name, bool Required, SchemaValue Value, string? Description);

/// <summary>
/// A value's advertised shape. <paramref name="Type"/> is the JSON type name, which is always
/// <c>string</c> here: the parse leaves every scalar as the text that was written, so a value that
/// is a date or a number in a client's eyes is a string in the note.
/// </summary>
/// <param name="Enum">The permitted values, where the constraint is an enumeration, and null
/// otherwise.</param>
/// <param name="Description">What the constraint wanted, in the words a violation would use.</param>
public sealed record SchemaValue(string Type, IReadOnlyList<string>? Enum, string? Description);
