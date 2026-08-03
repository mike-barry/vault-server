namespace VaultShared.Frontmatter;

/// <summary>
/// A note's content and a declaration in, a list of violations out, empty when valid. It needs
/// nothing else - no path, no filesystem, no consumer configuration.
///
/// <b>It never returns a verdict.</b> A boolean satisfies the single-note tools and strands the
/// sweeping one, which builds findings and remedy payloads from what was wrong, and the return shape
/// is set by the widest consumer. Each violation carries what a remedy needs without re-deriving it.
///
/// It is the whole-block extent over the checkers the write path applies per key - one variant
/// matcher, one constraint check - so a note this passes cannot be one an operation refuses over the
/// same key. What is compiled in rather than declared runs beside those checks rather than as a
/// second pass. Every key in the block is checked, and a key the declaration does not recognize is
/// itself a violation: this is a report on a note as it stands, not a gate. What an edit may write is
/// the write path's question, and it is scoped to the keys that edit touches.
/// </summary>
public static class FrontmatterValidator
{
    /// <summary>
    /// Everything wrong with <paramref name="content"/>'s frontmatter under
    /// <paramref name="registry"/>, in the order the block carries its keys.
    ///
    /// A block that will not parse is tolerated in the sense that matters - nothing throws, and one
    /// malformed note does not fail a whole-vault pass - and is reported as
    /// <see cref="ViolationRule.UnparsableBlock"/> rather than passed over. Answering "valid" for a
    /// note the write path refuses would break the agreement between the two extents, and the
    /// sweeping consumer this return shape exists for is exactly the one that needs to see it.
    /// </summary>
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
