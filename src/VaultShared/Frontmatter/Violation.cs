namespace VaultShared.Frontmatter;

/// <summary>
/// Represents one thing wrong with a note's frontmatter, reported as data rather than thrown as a
/// refusal. A sweeping tool builds findings and remedy payloads from these, which a boolean verdict
/// could not support.
/// </summary>
/// <param name="Rule">Which rule fired. An enumeration rather than a message, so a remedy branches
/// on it without reading prose.</param>
/// <param name="Key">The declared key the violation was found under.</param>
/// <param name="EntryPosition">Which entry within a block list, 1-based so findings from different
/// sources correlate, or null where the violation is not about one entry.</param>
/// <param name="Field">The offending sub-field, or null where the violation is about the entry or
/// the key as a whole.</param>
/// <param name="Reasoning">A human-readable explanation, for the person beside the remedy. Nothing
/// branches on this text.</param>
public sealed record Violation(ViolationRule Rule, string Key, int? EntryPosition, string? Field, string Reasoning);

/// <summary>
/// Identifies which rule a violation names. The first four hold for any block whatever the
/// vocabulary; the rest are the declaration applied.
/// </summary>
public enum ViolationRule
{
    /// <summary>
    /// The note has a block and it will not parse, or it parses to something other than a mapping.
    /// This is the one violation carrying no key, because there are no keys to carry.
    ///
    /// It is reported rather than passed over. Tolerance means the validator does not fail a
    /// whole-vault pass over one malformed note, not that it calls the note valid: the write path
    /// refuses this note, and a validator that passed it would break the rule that a note the
    /// validator passes is not one an operation refuses.
    /// </summary>
    UnparsableBlock,

    /// <summary>The declaration does not recognize this key. The validator skips nothing, because
    /// this is a report on a note as it stands rather than a gate.</summary>
    UnknownKey,

    /// <summary>A declared key carrying no value at all.</summary>
    DeclaredKeyWithNoValue,

    /// <summary>A block-list key whose list has no entries.</summary>
    EmptyBlockList,

    /// <summary>An inline value where the declaration says a block list belongs.</summary>
    InlineValueWhereBlockListDeclared,

    /// <summary>A block where the declaration says a scalar belongs - the mirror of
    /// <see cref="InlineValueWhereBlockListDeclared"/>, reported rather than refused because the
    /// validator reports on notes it did not write.</summary>
    BlockWhereScalarDeclared,

    /// <summary>A value that does not satisfy its declared constraint, on a scalar key or an entry
    /// field.</summary>
    ValueConstraintFailed,

    /// <summary>An entry matches none of its schema's variants. A bare scalar entry where no scalar
    /// variant is declared falls under this same rule rather than a special case.</summary>
    EntryMatchesNoVariant,

    /// <summary>An entry matches more than one variant, such as a mapping carrying two variants'
    /// discriminating fields. Matching none and matching several are each a violation rather than a
    /// fallback.</summary>
    EntryMatchesSeveralVariants,

    /// <summary>An entry that does not carry a field its variant declares required, either because
    /// the field is absent or because it is written with no value. A value that is null, empty, or
    /// whitespace only is empty, and the remedy is the same for both: supply text under the
    /// field.</summary>
    MissingRequiredField,

    /// <summary>An entry carrying a field its variant does not declare.</summary>
    UnknownField,

    /// <summary>An exclusive group in which the number of fields present is not exactly one,
    /// checked in both directions.</summary>
    ExclusiveGroupViolated,
}
