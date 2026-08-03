namespace VaultShared.Frontmatter;

/// <summary>
/// One thing wrong with a note's frontmatter, as data rather than a refusal. The return shape is set
/// by the widest consumer: a sweeping tool builds findings and remedy payloads from what was wrong,
/// and a boolean verdict would strand it.
/// </summary>
/// <param name="Rule">Which rule fired. An enumeration rather than a message, so a remedy branches
/// on it without reading prose.</param>
/// <param name="Key">The declared key it was found under.</param>
/// <param name="EntryPosition">Which entry within a block list, <b>1-based</b> so findings from
/// different sources correlate, or null where the violation is not about one entry.</param>
/// <param name="Field">The offending sub-field, or null where the violation is about the entry or
/// the key as a whole.</param>
/// <param name="Reasoning">Human-readable, for the person beside the remedy. Nothing branches on
/// this text.</param>
public sealed record Violation(ViolationRule Rule, string Key, int? EntryPosition, string? Field, string Reasoning);

/// <summary>
/// The rules a violation can name. The first four are compiled in - true of any block rather than of
/// a particular vocabulary - and the rest are the declaration, applied.
/// </summary>
public enum ViolationRule
{
    /// <summary>
    /// The note has a block and it will not parse, or parses to something other than a mapping. The
    /// one violation carrying no key, because there are no keys to carry.
    ///
    /// Reported rather than passed over silently. Tolerance means the validator does not fail a
    /// whole-vault pass over one malformed note, not that it calls that note valid: the write path
    /// refuses this note, and a validator that passed it would break the rule that a note the
    /// validator passes is not one an operation refuses.
    /// </summary>
    UnparsableBlock,

    /// <summary>The declaration does not recognize this key. The validator has no namespace of its
    /// own and skips nothing; this is a report on a note as it stands, not a gate.</summary>
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

    /// <summary>An entry matching none of its schema's variants. A bare scalar entry where no scalar
    /// variant is declared is this same check rather than a special case.</summary>
    EntryMatchesNoVariant,

    /// <summary>An entry matching more than one variant - a mapping carrying two variants'
    /// discriminating fields. Neither and both are each a violation rather than a fallback.</summary>
    EntryMatchesSeveralVariants,

    /// <summary>An entry missing a field its variant declares required.</summary>
    MissingRequiredField,

    /// <summary>An entry carrying a field its variant does not declare.</summary>
    UnknownField,

    /// <summary>A mutually exclusive group satisfied by neither one field nor exactly one - checked
    /// in both directions.</summary>
    ExclusiveGroupViolated,
}
