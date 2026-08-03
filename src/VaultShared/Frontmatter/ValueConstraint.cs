using System.Globalization;
using VaultShared.Access;

namespace VaultShared.Frontmatter;

/// <summary>
/// What a declared scalar key's text, or an entry field's, must satisfy. A closed hierarchy with a
/// checking member rather than a table of predicates keyed by kind name: the check and the
/// reporting of what it wanted are the same object, so a violation says more than that something
/// did not match.
///
/// The set is closed at five kinds and <b>there is no raw regular-expression kind and no extension
/// point for one</b>. The declaration is external input, so an uncompiled pattern out of a file is
/// both a denial-of-service surface and a source of violation messages no caller can act on. A
/// constraint needing one is added here as another named kind, compiled and time-bounded at load.
///
/// The same vocabulary serves scalar keys and entry fields. A block-list key takes none; its entry
/// schema carries them.
/// </summary>
public abstract record ValueConstraint
{
    /// <summary>Whether <paramref name="text"/> satisfies this constraint, and what it wanted when
    /// it does not. Runs against the scalar's text exactly as written, never a coerced value.</summary>
    public abstract ConstraintResult Check(string text);
}

/// <summary>
/// The answer a <see cref="ValueConstraint"/> gives, carrying what the constraint wanted rather
/// than only that something failed. <see cref="Wanted"/> is human-facing prose for a violation's
/// reasoning; a remedy branches on the violation's rule, never on this text.
/// </summary>
public sealed record ConstraintResult(bool IsSatisfied, string? Wanted)
{
    public static ConstraintResult Satisfied { get; } = new(true, null);

    public static ConstraintResult Unsatisfied(string wanted) => new(false, wanted);
}

/// <summary>
/// A format check on text, never a parse into a date. The value stays the string the caller wrote:
/// coercing it would test something they never wrote, and the round trip could rewrite their text.
/// </summary>
/// <param name="Format">A .NET custom date format string, matched whole against the value under
/// the invariant culture.</param>
public sealed record DateConstraint(string Format) : ValueConstraint
{
    /// <summary>What a declaration naming no format means. ISO 8601's calendar date, which is what
    /// the editor writes and what sorts correctly as text.</summary>
    public const string IsoDate = "yyyy-MM-dd";

    public override ConstraintResult Check(string text) =>
        DateTime.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? ConstraintResult.Satisfied
            : ConstraintResult.Unsatisfied($"a date written as {Format}");
}

/// <summary>
/// One of a fixed list of values, compared ordinally - the declaration lists the exact text
/// permitted, so a spelling that differs by case is a different value and not this one.
/// </summary>
public sealed record EnumerationConstraint(IReadOnlyList<string> Values) : ValueConstraint
{
    public override ConstraintResult Check(string text) =>
        Values.Contains(text, StringComparer.Ordinal)
            ? ConstraintResult.Satisfied
            : ConstraintResult.Unsatisfied($"one of: {string.Join(", ", Values)}");
}

/// <summary>
/// Every character drawn from a named class, with inclusive length bounds. The deliberate
/// placeholder shape: where no authoritative enumeration of a field's values exists yet, this stops
/// the field carrying smuggled text meanwhile, and tightening it later is an edit to the
/// declaration rather than a change to this layer.
/// </summary>
public sealed record CharClassConstraint(CharacterClass Class, int Min, int Max) : ValueConstraint
{
    public override ConstraintResult Check(string text)
    {
        var wanted = $"{Min} to {Max} characters from {Class.Spelling()}";
        if (text.Length < Min || text.Length > Max)
            return ConstraintResult.Unsatisfied(wanted);

        return text.All(c => Class.Admits(c)) ? ConstraintResult.Satisfied : ConstraintResult.Unsatisfied(wanted);
    }
}

/// <summary>
/// The character classes a <see cref="CharClassConstraint"/> may name, as a closed set. ASCII only:
/// the classes exist to bound what a field can carry, and a class admitting every Unicode letter
/// bounds very little.
/// </summary>
public enum CharacterClass
{
    /// <summary>ASCII letters.</summary>
    Alpha,

    /// <summary>ASCII letters and digits.</summary>
    Alphanumeric,

    /// <summary>ASCII letters, hyphen and underscore - the slug shape.</summary>
    AlphaDash,

    /// <summary>ASCII letters, digits, hyphen and underscore.</summary>
    AlphanumericDash,

    /// <summary>ASCII letters, digits, hyphen, underscore and the space character.</summary>
    AlphanumericDashSpace,
}

/// <summary>The class names as the declaration spells them, and what each admits.</summary>
public static class CharacterClasses
{
    /// <summary>The declaration's spelling for a class, or null when the name is not one of them -
    /// which the loader turns into a refusal.</summary>
    public static CharacterClass? Parse(string name) => name switch
    {
        "alpha" => CharacterClass.Alpha,
        "alnum" => CharacterClass.Alphanumeric,
        "alpha-dash" => CharacterClass.AlphaDash,
        "alnum-dash" => CharacterClass.AlphanumericDash,
        "alnum-dash-space" => CharacterClass.AlphanumericDashSpace,
        _ => null,
    };

    /// <summary>Every name a declaration may write, for a refusal that says what it could have said.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["alpha", "alnum", "alpha-dash", "alnum-dash", "alnum-dash-space"];

    public static string Spelling(this CharacterClass characterClass) => characterClass switch
    {
        CharacterClass.Alpha => "alpha",
        CharacterClass.Alphanumeric => "alnum",
        CharacterClass.AlphaDash => "alpha-dash",
        CharacterClass.AlphanumericDash => "alnum-dash",
        CharacterClass.AlphanumericDashSpace => "alnum-dash-space",
        _ => characterClass.ToString(),
    };

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
/// A wikilink in its required bracket form, and nothing else in the value. Recognition is the
/// layer's one wikilink grammar (<see cref="Wikilink"/>) rather than a second reading of what a
/// link is: a value this constraint admits and the census does not see would be a link nothing
/// maintains.
///
/// The embedding form is not admitted. A declared field holding a link references a note; an embed
/// is a rendering instruction, and the two are not interchangeable in a value a tool rewrites.
/// </summary>
public sealed record WikilinkConstraint : ValueConstraint
{
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
/// Text with nothing asserted about it. A declared key still requires this to be stated, because a
/// key declaring no constraint at all and a key declaring an unrecognized one would otherwise be
/// indistinguishable at load.
/// </summary>
public sealed record StringConstraint : ValueConstraint
{
    public override ConstraintResult Check(string text) => ConstraintResult.Satisfied;
}
