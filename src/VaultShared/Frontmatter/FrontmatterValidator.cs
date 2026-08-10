namespace VaultShared.Frontmatter;

/// <summary>
/// Validates a note's frontmatter against a declaration. It takes the note's content and the
/// declaration and needs nothing else - no path, no filesystem, no consumer configuration.
///
/// It runs the same checks the write path applies per key, over every key in the block, so a note
/// this passes cannot be one an operation refuses over the same key. A key the declaration does not
/// recognize is itself a violation, because this is a report on a note as it stands rather than a
/// gate. What an edit may write is the write path's question and is scoped to the keys that edit
/// touches.
/// </summary>
public static class FrontmatterValidator
{
    /// <summary>
    /// Returns everything wrong with a note's frontmatter, in the order the block carries its keys.
    /// </summary>
    /// <param name="content">The note's full content.</param>
    /// <param name="registry">The declaration to validate against.</param>
    /// <returns>The violations found, empty when the note is valid.</returns>
    /// <remarks>
    /// A block that will not parse throws nothing and does not fail a whole-vault pass. It is
    /// reported as <see cref="ViolationRule.UnparsableBlock"/> and is the only violation returned
    /// for that note.
    /// </remarks>
    public static IReadOnlyList<Violation> Validate(string content, FrontmatterRegistry registry)
    {
        var split = NoteSplit.Of(content);
        var violations = new List<Violation>();

        if (split.HasBlock)
        {
            var parse = FrontmatterTree.TryParse(split.InnerText, out _);
            if (parse != TreeParse.Parsed)
                return
                [
                    new Violation(
                        ViolationRule.UnparsableBlock, string.Empty, null, null,
                        parse == TreeParse.Unparsable
                            ? "The note's frontmatter block will not parse."
                            : "The note's frontmatter block is not a mapping of keys to values.")
                ];
        }

        var tree = FrontmatterTree.Read(content);
        foreach (var key in tree.Keys)
            violations.AddRange(FrontmatterChecks.CheckKey(registry, tree, key));

        return violations;
    }
}
