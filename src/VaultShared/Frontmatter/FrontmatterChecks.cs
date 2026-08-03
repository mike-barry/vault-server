namespace VaultShared.Frontmatter;

/// <summary>
/// One entry as the checks see it, whether it came out of a note or off an operation. Both sides run
/// the same variant matcher and the same field checks through this, which is what makes a note the
/// validator passes one no operation can refuse over the same key: there is one implementation of
/// each check rather than a reading side and a writing side that can drift.
/// </summary>
internal readonly struct EntryView
{
    private readonly FrontmatterEntry? _entry;
    private readonly EntryPayload? _payload;

    internal EntryView(FrontmatterEntry entry)
    {
        _entry = entry;
        _payload = null;
    }

    internal EntryView(EntryPayload payload)
    {
        _entry = null;
        _payload = payload;
    }

    internal bool IsScalar => _entry?.IsScalar ?? _payload is EntryPayload.Scalar;

    /// <summary>The entry's text where it is a bare scalar, and null where it is a mapping.</summary>
    internal string? Value => _entry is not null
        ? _entry.Value
        : (_payload as EntryPayload.Scalar)?.Value;

    /// <summary>The entry's field names, in order, empty where it is not a mapping.</summary>
    internal IReadOnlyList<string> Fields => _entry is not null
        ? _entry.Fields
        : (_payload as EntryPayload.Mapping)?.Fields.Select(f => f.Name).ToArray() ?? [];

    /// <summary>One field's text, matched case-insensitively, or null where it is not there.</summary>
    internal string? Field(string name) => _entry is not null
        ? _entry.Field(name)
        : (_payload as EntryPayload.Mapping)?.Field(name);
}

/// <summary>
/// The per-key checkers, shared by the write path and the validator. The write path runs them over
/// the keys an operation touched and the validator runs them over every key in the block; the extent
/// differs and the checks do not.
///
/// Everything here is a report. Which findings a caller-supplied value turns into a hard refusal is
/// the write path's decision, taken by reading these same violations.
/// </summary>
internal static class FrontmatterChecks
{
    /// <summary>
    /// Which variants of <paramref name="schema"/> an entry matches. Matching walks the variants
    /// once - a scalar node against the scalar variant, a mapping against the variant whose
    /// discriminating field it carries. Matching none and matching two are each a violation rather
    /// than a fallback, which is why this answers a list rather than a variant or null.
    /// </summary>
    internal static IReadOnlyList<EntryVariant> Match(EntrySchema schema, EntryView entry)
    {
        if (entry.IsScalar)
            return [.. schema.Variants.Where(v => v.Scalar is not null)];

        return
        [
            .. schema.Variants.Where(v =>
                v.DiscriminatedBy is { } discriminator && entry.Field(discriminator) is not null)
        ];
    }

    /// <summary>
    /// The identity value addressing this entry under <paramref name="variant"/>, or null where the
    /// variant declares no identity or the entry carries none of its identity fields. A variant
    /// with no identity is not addressable: nothing matches it, and a duplicate add under it is a
    /// second entry rather than a no-op.
    /// </summary>
    internal static string? IdentityOf(EntryVariant variant, EntryView entry)
    {
        foreach (var field in variant.Identity)
            if (entry.Field(field) is { } value && !OperationText.IsEmpty(value))
                return value;

        return null;
    }

    /// <summary>
    /// Everything wrong with one key: the compiled-in checks that are true of any block, and the
    /// declaration applied to whatever the key holds. An undeclared key is itself a violation and
    /// nothing further is checked under it - the declaration has nothing to say about its shape.
    /// </summary>
    internal static IReadOnlyList<Violation> CheckKey(FrontmatterRegistry registry, FrontmatterTree tree, string key)
    {
        if (!registry.Keys.TryGetValue(key, out var declaration))
            return [new Violation(ViolationRule.UnknownKey, key, null, null, "No declaration recognizes this key.")];

        var violations = new List<Violation>();
        var shape = tree.ShapeOf(key);

        if (declaration.Shape == KeyShape.Scalar)
        {
            if (shape == FrontmatterShape.Block)
            {
                violations.Add(new Violation(
                    ViolationRule.BlockWhereScalarDeclared, key, null, null,
                    "The declaration says this key holds a plain value, and the note gives it a block."));
                return violations;
            }

            var text = tree.Scalar(key) ?? string.Empty;
            if (OperationText.IsEmpty(text))
            {
                violations.Add(new Violation(
                    ViolationRule.DeclaredKeyWithNoValue, key, null, null, "The key is declared and carries no value."));
                return violations;
            }

            if (declaration.Value?.Check(text) is { IsSatisfied: false } refused)
                violations.Add(new Violation(
                    ViolationRule.ValueConstraintFailed, key, null, null, $"The value must be {refused.Wanted}."));

            return violations;
        }

        // A block-list key. The two compiled-in shape findings first, because neither leaves entries
        // to walk.
        if (shape == FrontmatterShape.Scalar)
        {
            violations.Add(new Violation(
                ViolationRule.InlineValueWhereBlockListDeclared, key, null, null,
                "The declaration says this key holds a block list, and the note gives it an inline value."));
            return violations;
        }

        var entries = tree.Entries(key);
        if (entries.Count == 0)
        {
            violations.Add(new Violation(
                ViolationRule.EmptyBlockList, key, null, null, "The block list has no entries."));
            return violations;
        }

        for (var i = 0; i < entries.Count; i++)
            violations.AddRange(CheckEntry(declaration.Entry!, new EntryView(entries[i]), key, i + 1));

        return violations;
    }

    /// <summary>
    /// One entry against its schema: exactly one variant, then that variant's required fields,
    /// unknown fields, exclusive groups in both directions, and each field's value constraint.
    /// <paramref name="position"/> is 1-based, as every entry position reported to a human is.
    /// </summary>
    internal static IReadOnlyList<Violation> CheckEntry(EntrySchema schema, EntryView entry, string key, int position)
    {
        var matched = Match(schema, entry);
        if (matched.Count == 0)
            return
            [
                new Violation(
                    ViolationRule.EntryMatchesNoVariant, key, position, null,
                    entry.IsScalar
                        ? "The entry is a bare value and the schema declares no scalar variant."
                        : $"The entry carries no declared discriminating field - one of: {Discriminators(schema)}.")
            ];

        if (matched.Count > 1)
            return
            [
                new Violation(
                    ViolationRule.EntryMatchesSeveralVariants, key, position, null,
                    $"The entry matches more than one variant: {string.Join(", ", matched.Select(v => v.Name))}.")
            ];

        var variant = matched[0];
        var violations = new List<Violation>();

        if (entry.IsScalar)
        {
            var text = entry.Value ?? string.Empty;
            if (variant.Scalar?.Check(text) is { IsSatisfied: false } refused)
                violations.Add(new Violation(
                    ViolationRule.ValueConstraintFailed, key, position, null, $"The entry must be {refused.Wanted}."));

            return violations;
        }

        foreach (var (name, field) in variant.Fields)
            if (field.Required && entry.Field(name) is null)
                violations.Add(new Violation(
                    ViolationRule.MissingRequiredField, key, position, name,
                    $"Variant \"{variant.Name}\" requires the field \"{name}\"."));

        foreach (var name in entry.Fields)
            if (!variant.Fields.ContainsKey(name))
                violations.Add(new Violation(
                    ViolationRule.UnknownField, key, position, name,
                    $"Variant \"{variant.Name}\" declares no field \"{name}\"."));

        if (variant.ExactlyOneOf.Count > 0)
        {
            var present = variant.ExactlyOneOf.Where(name => entry.Field(name) is not null).ToArray();
            if (present.Length != 1)
                violations.Add(new Violation(
                    ViolationRule.ExclusiveGroupViolated, key, position, null,
                    present.Length == 0
                        ? $"Exactly one of {string.Join(", ", variant.ExactlyOneOf)} must be present, and none is."
                        : $"Exactly one of {string.Join(", ", variant.ExactlyOneOf)} must be present, and these are: {string.Join(", ", present)}."));
        }

        foreach (var (name, field) in variant.Fields)
        {
            if (entry.Field(name) is not { } text)
                continue;
            if (field.Value?.Check(text) is { IsSatisfied: false } refused)
                violations.Add(new Violation(
                    ViolationRule.ValueConstraintFailed, key, position, name, $"The field must be {refused.Wanted}."));
        }

        return violations;
    }

    private static string Discriminators(EntrySchema schema) =>
        string.Join(", ", schema.Variants.Select(v => v.DiscriminatedBy).Where(d => d is not null));
}
