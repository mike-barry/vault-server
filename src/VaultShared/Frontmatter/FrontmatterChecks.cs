namespace VaultShared.Frontmatter;

/// <summary>
/// Presents one entry to the checks, whether it came out of a note or off an operation. Both sides
/// run the same variant matcher and the same field checks through this view, so a note the
/// validator passes cannot be one an operation refuses over the same key.
/// </summary>
internal readonly struct EntryView
{
    private readonly FrontmatterEntry? _entry;
    private readonly EntryPayload? _payload;

    /// <summary>Initializes a view over an entry read from a note.</summary>
    /// <param name="entry">The entry as it stands in the note.</param>
    internal EntryView(FrontmatterEntry entry)
    {
        _entry = entry;
        _payload = null;
    }

    /// <summary>Initializes a view over an entry an operation is about to add.</summary>
    /// <param name="payload">The entry the caller supplied.</param>
    internal EntryView(EntryPayload payload)
    {
        _entry = null;
        _payload = payload;
    }

    /// <summary>Gets whether the entry is a bare scalar rather than a mapping of fields.</summary>
    internal bool IsScalar => _entry?.IsScalar ?? _payload is EntryPayload.Scalar;

    /// <summary>Gets the entry's text where it is a bare scalar, and null where it is a mapping.</summary>
    internal string? Value => _entry is not null
        ? _entry.Value
        : (_payload as EntryPayload.Scalar)?.Value;

    /// <summary>Gets the entry's field names, in order, empty where it is not a mapping.</summary>
    internal IReadOnlyList<string> Fields => _entry is not null
        ? _entry.Fields
        : (_payload as EntryPayload.Mapping)?.Fields.Select(f => f.Name).ToArray() ?? [];

    /// <summary>Returns one field's text.</summary>
    /// <param name="name">The field name, matched case-insensitively.</param>
    /// <returns>The field's text, or null where the entry does not carry it.</returns>
    internal string? Field(string name) => _entry is not null
        ? _entry.Field(name)
        : (_payload as EntryPayload.Mapping)?.Field(name);
}

/// <summary>
/// Checks one frontmatter key against its declaration. The write path and the validator share these
/// checks: the write path runs them over the keys an operation touched and the validator runs them
/// over every key in the block, so the extent differs and the checks do not.
///
/// Everything here is reported as data. Which findings become a hard refusal is the write path's
/// decision, taken by reading these same violations.
/// </summary>
internal static class FrontmatterChecks
{
    /// <summary>
    /// Returns which variants of <paramref name="schema"/> an entry matches. Matching walks the
    /// variants once: a scalar node against the scalar variant, a mapping against the variant whose
    /// discriminating field it carries.
    /// </summary>
    /// <param name="schema">The schema whose variants are matched.</param>
    /// <param name="entry">The entry to match.</param>
    /// <returns>The matching variants. Matching none and matching several are each a violation
    /// rather than a fallback, so this returns a list instead of a variant or null.</returns>
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

    /// <summary>Returns the identity value that addresses this entry under
    /// <paramref name="variant"/>.</summary>
    /// <param name="variant">The variant the entry matched.</param>
    /// <param name="entry">The entry to read the identity from.</param>
    /// <returns>The first non-empty identity field's value, or null where the variant declares no
    /// identity or the entry carries none of its identity fields. An entry with no identity cannot
    /// be addressed, so a duplicate add under it produces a second entry rather than a
    /// no-op.</returns>
    internal static string? IdentityOf(EntryVariant variant, EntryView entry)
    {
        foreach (var field in variant.Identity)
            if (entry.Field(field) is { } value && !OperationText.IsEmpty(value))
                return value;

        return null;
    }

    /// <summary>
    /// Returns everything wrong with one key: the compiled-in checks that hold for any block, and
    /// the declaration applied to whatever the key holds.
    /// </summary>
    /// <param name="registry">The vault's loaded declaration.</param>
    /// <param name="tree">The frontmatter the key lives in.</param>
    /// <param name="key">The key to check.</param>
    /// <returns>The violations found, empty where the key is valid. An undeclared key is itself a
    /// violation and nothing further is checked under it, because the declaration has nothing to
    /// say about its shape.</returns>
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

        // A block-list key. The two shape findings come first, because neither leaves entries to
        // walk.
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
    /// Checks one entry against its schema: exactly one variant, then that variant's required
    /// fields, unknown fields, exclusive groups in both directions, and each field's value
    /// constraint.
    /// </summary>
    /// <param name="schema">The schema the entry must satisfy.</param>
    /// <param name="entry">The entry to check.</param>
    /// <param name="key">The block-list key the entry lives under, carried onto each violation.</param>
    /// <param name="position">The entry's position, 1-based as every entry position reported to a
    /// person is.</param>
    /// <returns>The violations found, empty where the entry is valid.</returns>
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

        // A required field is satisfied by text rather than by presence. A value that is null,
        // empty, or whitespace only is empty, so a required field written with nothing under it is
        // reported exactly as an absent one: an entry whose identity field is empty has no identity
        // to address, and nothing can then name it for removal.
        foreach (var (name, field) in variant.Fields)
        {
            if (!field.Required)
                continue;
            if (entry.Field(name) is not { } text)
                violations.Add(new Violation(
                    ViolationRule.MissingRequiredField, key, position, name,
                    $"Variant \"{variant.Name}\" requires the field \"{name}\"."));
            else if (OperationText.IsEmpty(text))
                violations.Add(new Violation(
                    ViolationRule.MissingRequiredField, key, position, name,
                    $"Variant \"{variant.Name}\" requires a value for the field \"{name}\"."));
        }

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
