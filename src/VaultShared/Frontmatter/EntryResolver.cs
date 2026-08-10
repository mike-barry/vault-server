namespace VaultShared.Frontmatter;

/// <summary>Represents one entry of a block list, together with what the declaration makes of it.</summary>
/// <param name="Position">Its index in the list, 0-based. This is an internal coordinate for a
/// removal or an insertion, never a caller-facing address; entry positions reported to people are
/// 1-based and live on <see cref="Violation"/>.</param>
/// <param name="Variant">The single variant it matches, or null where it matches none or several.
/// An entry with no variant has no identity and cannot be addressed.</param>
/// <param name="Identity">The value that addresses it, or null where its variant declares no
/// identity or the entry carries none of the identity fields.</param>
internal sealed record ResolvedEntry(int Position, EntryVariant? Variant, string? Identity);

/// <summary>
/// Resolves entries of a block list by their declared identity. Every identity question goes
/// through this type - whether an entry already exists, which entry a removal names, where an
/// anchor points - so a duplicate add and a removal cannot disagree about whether an entry is
/// there.
/// </summary>
internal static class EntryResolver
{
    /// <summary>Returns every entry under <paramref name="key"/>, with its variant and identity.</summary>
    /// <param name="tree">The frontmatter to read.</param>
    /// <param name="key">The block-list key whose entries are resolved.</param>
    /// <param name="schema">The schema whose variants the entries are matched against.</param>
    /// <returns>The entries in list order, empty where the key holds no block list.</returns>
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
    /// Returns the entries that <paramref name="identity"/> addresses. Each entry is compared under
    /// its own variant's declared comparison, so a list holding a case-insensitive variant beside a
    /// case-sensitive one answers each the way its declaration says.
    /// </summary>
    /// <param name="entries">The resolved entries to search.</param>
    /// <param name="identity">The addressing value to match.</param>
    /// <returns>The matching entries, empty where none match.</returns>
    internal static IReadOnlyList<ResolvedEntry> Matching(IReadOnlyList<ResolvedEntry> entries, string identity) =>
    [
        .. entries.Where(e =>
            e.Identity is not null && e.Variant is not null
            && string.Equals(e.Identity, identity, e.Variant.IdentityMatch))
    ];
}
