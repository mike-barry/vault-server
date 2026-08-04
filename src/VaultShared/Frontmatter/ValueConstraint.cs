using System.Globalization;
using VaultShared.Access;

namespace VaultShared.Frontmatter;

/// <summary>
/// Represents what a declared scalar key's text, or an entry field's, must satisfy. This is a
/// closed hierarchy with a checking member rather than a table of predicates keyed by kind name, so
/// the check and the description of what it wanted are the same object and a violation can say more
/// than that something did not match.
///
/// The set is closed at five kinds. There is deliberately no raw regular-expression kind and no
/// extension point for one: the declaration is external input, so an uncompiled pattern read from a
/// file is both a denial-of-service surface and a source of violation messages no caller can act
/// on. A constraint needing a pattern is added here as another named kind, compiled and
/// time-bounded at load.
///
/// The same set serves scalar keys and entry fields. A block-list key takes no constraint of its
/// own; its entry schema carries them.
/// </summary>
public abstract record ValueConstraint
{
    /// <summary>Checks text against this constraint. The check runs against the text exactly as
    /// written, never a coerced value.</summary>
    /// <param name="text">The text to check.</param>
    /// <returns>Whether the text is satisfactory, and what the constraint wanted when it is
    /// not.</returns>
    public abstract ConstraintResult Check(string text);
}

/// <summary>
/// Represents the answer a <see cref="ValueConstraint"/> gives, carrying what the constraint wanted
/// rather than only that something failed.
/// </summary>
/// <param name="IsSatisfied">Whether the text satisfied the constraint.</param>
/// <param name="Wanted">What the constraint wanted, as human-facing prose for a violation's
/// reasoning, or null when the text was satisfactory. A remedy branches on the violation's rule,
/// never on this text.</param>
public sealed record ConstraintResult(bool IsSatisfied, string? Wanted)
{
    /// <summary>Gets the result for text that satisfied the constraint.</summary>
    public static ConstraintResult Satisfied { get; } = new(true, null);

    /// <summary>Returns a result for text that did not satisfy the constraint.</summary>
    /// <param name="wanted">What the constraint wanted.</param>
    /// <returns>The unsatisfied result.</returns>
    public static ConstraintResult Unsatisfied(string wanted) => new(false, wanted);
}

/// <summary>
/// Checks that text matches a date format, without parsing it into a date. The value stays the
/// string the caller wrote, because coercing it would test something they never wrote and the round
/// trip could rewrite their text.
/// </summary>
/// <param name="Format">A .NET custom date format string, matched whole against the value under
/// the invariant culture.</param>
public sealed record DateConstraint(string Format) : ValueConstraint
{
    /// <summary>The format a declaration naming none gets: the ISO 8601 calendar date, which is
    /// what the editor writes and what sorts correctly as text.</summary>
    public const string IsoDate = "yyyy-MM-dd";

    /// <inheritdoc/>
    public override ConstraintResult Check(string text) =>
        DateTime.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? ConstraintResult.Satisfied
            : ConstraintResult.Unsatisfied($"a date written as {Format}");
}

/// <summary>
/// Checks that text is one of a fixed list of values, compared ordinally. The declaration lists the
/// exact text permitted, so a spelling that differs by case is a different value.
/// </summary>
/// <param name="Values">The permitted values.</param>
public sealed record EnumerationConstraint(IReadOnlyList<string> Values) : ValueConstraint
{
    /// <inheritdoc/>
    public override ConstraintResult Check(string text) =>
        Values.Contains(text, StringComparer.Ordinal)
            ? ConstraintResult.Satisfied
            : ConstraintResult.Unsatisfied($"one of: {string.Join(", ", Values)}");
}

/// <summary>
/// Checks that every character is drawn from a named class and that the length falls within
/// inclusive bounds. This is the placeholder shape for a field with no authoritative list of values
/// yet: it stops the field carrying smuggled text meanwhile, and tightening it later is an edit to
/// the declaration rather than a change to this layer.
/// </summary>
/// <param name="Class">The character class every character must belong to.</param>
/// <param name="Min">The shortest permitted length, inclusive.</param>
/// <param name="Max">The longest permitted length, inclusive.</param>
public sealed record CharClassConstraint(CharacterClass Class, int Min, int Max) : ValueConstraint
{
    /// <inheritdoc/>
    public override ConstraintResult Check(string text)
    {
        var wanted = $"{Min} to {Max} characters from {Class.Spelling()}";
        if (text.Length < Min || text.Length > Max)
            return ConstraintResult.Unsatisfied(wanted);

        return text.All(c => Class.Admits(c)) ? ConstraintResult.Satisfied : ConstraintResult.Unsatisfied(wanted);
    }
}

/// <summary>
/// Identifies the character classes a <see cref="CharClassConstraint"/> may name. The set is closed
/// and ASCII only, because the classes exist to bound what a field can carry and a class admitting
/// every Unicode letter bounds very little.
/// </summary>
public enum CharacterClass
{
    /// <summary>ASCII letters.</summary>
    Alpha,

    /// <summary>ASCII letters and digits.</summary>
    Alphanumeric,

    /// <summary>ASCII letters, hyphen and underscore.</summary>
    AlphaDash,

    /// <summary>ASCII letters, digits, hyphen and underscore.</summary>
    AlphanumericDash,

    /// <summary>ASCII letters, digits, hyphen, underscore and the space character.</summary>
    AlphanumericDashSpace,
}

/// <summary>Maps between the class names a declaration spells and what each class admits.</summary>
public static class CharacterClasses
{
    /// <summary>Returns the class a declaration's spelling names.</summary>
    /// <param name="name">The spelling as written in the declaration.</param>
    /// <returns>The class, or null when the name is not one of them, which the loader turns into a
    /// refusal.</returns>
    public static CharacterClass? Parse(string name) => name switch
    {
        "alpha" => CharacterClass.Alpha,
        "alnum" => CharacterClass.Alphanumeric,
        "alpha-dash" => CharacterClass.AlphaDash,
        "alnum-dash" => CharacterClass.AlphanumericDash,
        "alnum-dash-space" => CharacterClass.AlphanumericDashSpace,
        _ => null,
    };

    /// <summary>Gets every name a declaration may write, so a refusal can list the alternatives.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["alpha", "alnum", "alpha-dash", "alnum-dash", "alnum-dash-space"];

    /// <summary>Returns the declaration's spelling for a class.</summary>
    /// <param name="characterClass">The class to spell.</param>
    /// <returns>The name a declaration writes for it.</returns>
    public static string Spelling(this CharacterClass characterClass) => characterClass switch
    {
        CharacterClass.Alpha => "alpha",
        CharacterClass.Alphanumeric => "alnum",
        CharacterClass.AlphaDash => "alpha-dash",
        CharacterClass.AlphanumericDash => "alnum-dash",
        CharacterClass.AlphanumericDashSpace => "alnum-dash-space",
        _ => characterClass.ToString(),
    };

    /// <summary>Returns whether a class admits a character.</summary>
    /// <param name="characterClass">The class to test against.</param>
    /// <param name="c">The character to test.</param>
    /// <returns>True when the class admits the character.</returns>
    public static bool Admits(this CharacterClass characterClass, char c) => characterClass switch
    {
        CharacterClass.Alpha => IsAsciiLetter(c),
        CharacterClass.Alphanumeric => IsAsciiLetter(c) || char.IsAsciiDigit(c),
        CharacterClass.AlphaDash => IsAsciiLetter(c) || c is '-' or '_',
        CharacterClass.AlphanumericDash => IsAsciiLetter(c) || char.IsAsciiDigit(c) || c is '-' or '_',
        CharacterClass.AlphanumericDashSpace => IsAsciiLetter(c) || char.IsAsciiDigit(c) || c is '-' or '_' or ' ',
        _ => false,
    };

    private static bool IsAsciiLetter(char c) => char.IsAsciiLetter(c);
}

/// <summary>
/// Checks that the value is a wikilink in bracket form and nothing else. Recognition goes through
/// the layer's single wikilink grammar (<see cref="Wikilink"/>) rather than a second reading of
/// what a link is, so this constraint cannot admit a value the link census does not see.
///
/// The embedding form is not admitted. A declared field holding a link references a note, while an
/// embed is a rendering instruction, and the two are not interchangeable in a value a tool
/// rewrites.
/// </summary>
public sealed record WikilinkConstraint : ValueConstraint
{
    /// <inheritdoc/>
    public override ConstraintResult Check(string text)
    {
        const string wanted = "a wikilink in bracket form, as [[Target]]";
        var occurrences = Wikilink.Recognize(text);
        if (occurrences.Count != 1)
            return ConstraintResult.Unsatisfied(wanted);

        var only = occurrences[0];
        return only.Start == 0 && only.Length == text.Length && text.StartsWith("[[", StringComparison.Ordinal)
            ? ConstraintResult.Satisfied
            : ConstraintResult.Unsatisfied(wanted);
    }
}

/// <summary>
/// Admits any text. A declaration must still state this kind explicitly, because a key declaring no
/// constraint at all and a key declaring an unrecognized one would otherwise be indistinguishable
/// at load.
/// </summary>
public sealed record StringConstraint : ValueConstraint
{
    /// <inheritdoc/>
    public override ConstraintResult Check(string text) => ConstraintResult.Satisfied;
}
