using System.Text.RegularExpressions;

namespace VaultShared.Access;

/// <summary>
/// The one grammar every name this layer brings into existence must satisfy: a creation target, a
/// folder, or a move destination. It is checked on that single segment and nowhere else - every
/// other segment of a path already exists, and names already on disk are never checked, so a file
/// predating the grammar stays readable, searchable, and movable from.
///
/// The grammar exists for containment, not style. A name is substituted into note text and into
/// quoted frontmatter values without parsing either, so a name holding a quote, a bracket, or a
/// line break could end the construct around it and corrupt the note. A conforming name cannot,
/// which is what lets the substitution skip escaping.
/// </summary>
public static partial class VaultName
{
    /// <summary>The extension a note carries. Lower-case and ordinal: this is a name being created.</summary>
    public const string NoteExtension = ".md";

    /// <summary>
    /// A folder's name: the grammar alone - one or more groups joined by single dashes, each group
    /// one or more ASCII letters, digits, <c>+</c> or <c>&amp;</c>, with an upper-case letter or a
    /// digit first. Nothing else: no spaces, no underscores, no other punctuation, no non-ASCII, no
    /// leading or trailing dash.
    /// </summary>
    public static bool IsFolderName(string name) => Grammar().IsMatch(name);

    /// <summary>A note's name: the grammar as the stem, then <see cref="NoteExtension"/>.</summary>
    public static bool IsNoteName(string name) =>
        name.EndsWith(NoteExtension, StringComparison.Ordinal)
        && Grammar().IsMatch(name[..^NoteExtension.Length]);

    /// <summary>
    /// A conforming spelling derived from what the caller sent, or null where none can be. A
    /// refusal names it so the miss costs one retry rather than a reading of the grammar; choosing
    /// among conforming names stays the caller's, which is why this offers exactly one and offers
    /// nothing where the text carries no conforming characters at all.
    ///
    /// The derivation is mechanical: every run of characters the grammar does not admit becomes a
    /// single dash, dashes at either end are dropped, and a leading lower-case letter is raised.
    /// Nothing is transliterated - a name that is entirely non-ASCII has no derivation, and
    /// inventing one would be this build choosing what a note is called.
    /// </summary>
    /// <param name="name">The final segment as the caller spelled it, with the note extension where
    /// it is a note's.</param>
    public static string? Suggest(string name)
    {
        var isNote = name.EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase);
        var stem = isNote ? name[..^NoteExtension.Length] : name;

        var groups = new List<string>();
        var group = new System.Text.StringBuilder();
        foreach (var character in stem)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '+' or '&')
            {
                group.Append(character);
                continue;
            }

            if (group.Length > 0)
                groups.Add(group.ToString());
            group.Clear();
        }
        if (group.Length > 0)
            groups.Add(group.ToString());

        // The first character must be an upper-case letter or a digit, so a leading group made
        // entirely of "+" and "&" is trimmed away rather than the name being refused outright.
        var derived = string.Join('-', groups).TrimStart('+', '&', '-');
        if (derived.Length == 0)
            return null;

        derived = char.ToUpperInvariant(derived[0]) + derived[1..];
        if (!Grammar().IsMatch(derived))
            return null;

        var suggested = isNote ? derived + NoteExtension : derived;
        return string.Equals(suggested, name, StringComparison.Ordinal) ? null : suggested;
    }

    // \A and \z rather than ^ and $, which would admit a trailing newline inside the name.
    [GeneratedRegex(@"\A[A-Z0-9][A-Za-z0-9+&]*(?:-[A-Za-z0-9+&]+)*\z")]
    private static partial Regex Grammar();
}
