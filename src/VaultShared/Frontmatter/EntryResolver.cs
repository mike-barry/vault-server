namespace VaultShared.Frontmatter;

/// <summary>One entry of a block list, with what the declaration makes of it.</summary>
/// <param name="Position">Its index in the list, 0-based - an internal coordinate for a removal or
/// an insertion, never a caller-facing address. Entry positions reported to humans are 1-based and
/// live on <see cref="Violation"/>.</param>
/// <param name="Variant">The single variant it matches, or null where it matches none or several.
/// An entry with no variant has no identity and is addressable by nothing.</param>
/// <param name="Identity">The value addressing it, or null where its variant declares no identity
/// or it carries none of the identity fields.</param>
internal sealed record ResolvedEntry(int Position, EntryVariant? Variant, string? Identity);

/// <summary>
/// The one resolver every identity question goes through - does this entry already exist, which
/// entry does a removal name, where does an anchor point. One implementation, so a duplicate add and
/// a removal cannot disagree about whether an entry is there.
///
/// Comparison is the <see cref="EntryVariant.IdentityMatch"/> the declaration puts on the entry's
/// own variant, chosen there because the right answer differs within a single list: an identity
/// naming a file or a wikilink compares as the filesystem and the editor resolve it, and a line of
/// human prose compares exactly.
/// </summary>
internal static class EntryResolver
{
    /// <summary>Every entry under <paramref name="key"/>, with its variant and identity.</summary>
    internal static IReadOnlyList<ResolvedEntry> Entries(FrontmatterTree tree, string key, EntrySchema schema)
    {
        var entries = tree.Entries(key);
        var resolved = new List<ResolvedEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var view = new EntryView(entries[i]);
            var matched = FrontmatterChecks.Match(schema, view);
            var variant = matched.Count == 1 ? matched[0] : null;
            resolved.Add(new ResolvedEntry(i, variant, variant is null ? null : FrontmatterChecks.IdentityOf(variant, view)));
        }

        return resolved;
    }

    /// <summary>
    /// The entries <paramref name="identity"/> addresses. Every entry is compared under its own
    /// variant's declared comparison, so one list holding a case-insensitive variant beside a
    /// case-sensitive one answers each the way its declaration says.
    /// </summary>
    internal static IReadOnlyList<ResolvedEntry> Matching(IReadOnlyList<ResolvedEntry> entries, string identity) =>
    [
        .. entries.Where(e =>
            e.Identity is not null && e.Variant is not null
            && string.Equals(e.Identity, identity, e.Variant.IdentityMatch))
    ];
}
