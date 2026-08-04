using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// Represents one link whose target text changed, and where. A repair on relocation and a pin on
/// creation both report this shape.
/// </summary>
/// <remarks>
/// Carries identities and positions only, never note content. A pass reaches notes the caller's
/// grant cannot read, so nothing beyond the path, the line, and the two target texts may be
/// reported out of it: no surrounding text and no note excerpt.
/// </remarks>
/// <param name="Path">The vault-relative path of the note holding the link, usable in a later call.</param>
/// <param name="Line">The one-based line the link sits on, matching how the search surfaces count.</param>
/// <param name="From">The target as the note carried it.</param>
/// <param name="To">The target the note now carries.</param>
public sealed record LinkRewrite(string Path, int Line, string From, string To);

/// <summary>
/// Represents one link the census found, and where. The census changes nothing; the caller decides
/// what to do about each occurrence.
/// </summary>
/// <param name="Path">The vault-relative path of the note holding the link, usable in a later call.</param>
/// <param name="Line">The one-based line the link sits on.</param>
/// <param name="Target">The target as the note carries it.</param>
public sealed record LinkOccurrence(string Path, int Line, string Target);

/// <summary>
/// Represents one note's path before and after a relocation. A note relocation produces one of
/// these; a folder relocation produces one per contained note.
/// </summary>
/// <param name="From">Where the note is now.</param>
/// <param name="To">Where the note will be.</param>
public sealed record NoteRelocation(VaultPath From, VaultPath To);
