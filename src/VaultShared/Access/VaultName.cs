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

    // \A and \z rather than ^ and $, which would admit a trailing newline inside the name.
    [GeneratedRegex(@"\A[A-Z0-9][A-Za-z0-9+&]*(?:-[A-Za-z0-9+&]+)*\z")]
    private static partial Regex Grammar();
}
