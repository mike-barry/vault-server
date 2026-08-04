namespace VaultShared.Frontmatter;

/// <summary>
/// Validates a note's frontmatter against a declaration. It takes the note's content and the
/// declaration and needs nothing else - no path, no filesystem, no consumer configuration.
///
/// It returns a list of violations rather than a boolean verdict. A boolean would satisfy the
/// single-note tools but strand the sweeping one, which builds findings and remedy payloads from
/// what was wrong, and each violation carries what a remedy needs without re-deriving it.
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
    /// A block that will not parse is tolerated in the sense that nothing throws and one malformed
    /// note does not fail a whole-vault pass, but it is still reported as
    /// <see cref="ViolationRule.UnparsableBlock"/>. Answering "valid" for a note the write path
    /// refuses would break the agreement between the two, and the sweeping consumer is exactly the
    /// one that needs to see it.
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
