using System.Text.RegularExpressions;

namespace VaultShared.Access;

/// <summary>
/// Defines the grammar every name this layer creates must satisfy: a note being created, a folder,
/// or a move destination. Only that one new segment is checked. Names already on disk are never
/// checked, so a file predating the grammar stays readable, searchable, and movable.
/// </summary>
/// <remarks>
/// The grammar exists to keep substitution safe, not to enforce a house style. A name is
/// substituted into note text and into quoted frontmatter values without either being parsed, so a
/// name containing a quote, a bracket, or a line break could terminate the construct around it and
/// corrupt the note. A conforming name cannot, which is what lets the substitution skip escaping.
/// </remarks>
public static partial class VaultName
{
    /// <summary>The extension a note carries. Lower-case, because this is a name being created.</summary>
    public const string NoteExtension = ".md";

    /// <summary>
    /// Returns whether a name is a valid folder name: one or more groups joined by single dashes,
    /// each group one or more ASCII letters, digits, <c>+</c> or <c>&amp;</c>, starting with an
    /// upper-case letter or a digit. No spaces, underscores, other punctuation, non-ASCII
    /// characters, or leading or trailing dashes.
    /// </summary>
    /// <param name="name">The single path segment to check.</param>
    /// <returns>True when the name satisfies the grammar.</returns>
    public static bool IsFolderName(string name) => Grammar().IsMatch(name);

    /// <summary>
    /// Returns whether a name is a valid note name: the folder grammar as the stem, followed by
    /// <see cref="NoteExtension"/>.
    /// </summary>
    /// <param name="name">The single path segment to check.</param>
    /// <returns>True when the name satisfies the grammar and carries the note extension.</returns>
    public static bool IsNoteName(string name) =>
        name.EndsWith(NoteExtension, StringComparison.Ordinal)
        && Grammar().IsMatch(name[..^NoteExtension.Length]);

    /// <summary>
    /// Derives one conforming spelling from a name the grammar refuses, so a refusal can name it
    /// and the caller can retry without reading the grammar. Exactly one suggestion is offered;
    /// choosing among conforming names stays the caller's decision.
    /// </summary>
    /// <remarks>
    /// The derivation is mechanical: every run of characters the grammar does not admit becomes a
    /// single dash, dashes at either end are dropped, and a leading lower-case letter is raised.
    /// Nothing is transliterated, so a name that is entirely non-ASCII has no suggestion rather
    /// than one this code invented.
    /// </remarks>
    /// <param name="name">
    /// The final path segment as the caller spelled it, carrying the note extension where it names
    /// a note.
    /// </param>
    /// <returns>
    /// A conforming name, or null when none can be derived or when the name already conforms.
    /// </returns>
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
