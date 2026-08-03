using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// One link whose target text changed, and where. Both a repair on relocation and a pin on creation
/// report this shape, because both are the same event: a target that said one thing now says
/// another.
///
/// Identities and positions, never content. A pass reaches notes the caller's grant cannot read, so
/// what leaves it is the path, the line, and the two target texts - values the caller already named
/// or the operation itself produced. No surrounding text and no note excerpt.
/// </summary>
/// <param name="Path">Vault-relative, and quotable back into a later call.</param>
/// <param name="Line">One-based, as the search surfaces count and as an editor numbers its gutter.</param>
/// <param name="From">The target as the note carried it.</param>
/// <param name="To">The target the note now carries.</param>
public sealed record LinkRewrite(string Path, int Line, string From, string To);

/// <summary>
/// One link the census found, and where. Nothing was changed: the census reports and the caller
/// decides, which is the whole of removal's asymmetry with creation.
/// </summary>
/// <param name="Path">Vault-relative, and quotable back into a later call.</param>
/// <param name="Line">One-based, as <see cref="LinkRewrite.Line"/> is.</param>
/// <param name="Target">The target as the note carries it.</param>
public sealed record LinkOccurrence(string Path, int Line, string Target);

/// <summary>
/// One note's before and after under a relocation. A note relocation produces one of these; a
/// folder relocation produces one per contained note, since a folder's contents move with it.
/// </summary>
public sealed record NoteRelocation(VaultPath From, VaultPath To);
