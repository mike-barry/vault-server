using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>PathPolicy.MutationRequires</c>, which matches a path against a policy's
/// <c>mutation_requires</c> entries.
/// </summary>
/// <remarks>
/// Every enforcer shares this one matcher: the frontmatter write path consults it alongside the
/// authority assertion, and the tools that remove or relocate consult it during validation.
/// <c>PathPolicyTests</c> covers how a policy entry is parsed; these tests cover what a match
/// means.
/// </remarks>
public sealed class PathPolicyMutationMatchTests
{
    private const string Elevated = "agentic:write";
    private const string Content = "content:write";

    private static PathPolicy Policy(string entries) => Synthetic.Policy(entries);

    [Fact]
    public void APathEntryMatchesThatFileAndEverythingBeneathIt()
    {
        var policy = Policy($$"""[ { "path": "Areas/Agentic", "mutation_requires": "{{Elevated}}" } ]""");

        Assert.Equal([Elevated], policy.MutationRequires("Areas/Agentic"));
        Assert.Equal([Elevated], policy.MutationRequires("Areas/Agentic/Deep/Note.md"));
    }

    [Fact]
    public void MatchingIsByWholeSegment()
    {
        // A raw string prefix would catch a similarly named sibling directory.
        var policy = Policy($$"""[ { "path": "Areas/Agentic", "mutation_requires": "{{Elevated}}" } ]""");

        // The entry is live, so the answer below is a non-match rather than an empty policy.
        Assert.Equal([Elevated], policy.MutationRequires("Areas/Agentic/Note.md"));
        Assert.Empty(policy.MutationRequires("Areas/Agentic-Old/Note.md"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndSeparatorAgnostic()
    {
        // A caller must not reach a protected file by spelling it differently.
        var policy = Policy($$"""[ { "path": "Areas/Agentic", "mutation_requires": "{{Elevated}}" } ]""");

        Assert.Equal([Elevated], policy.MutationRequires("areas/AGENTIC/Note.md"));
        Assert.Equal([Elevated], policy.MutationRequires(@"Areas\Agentic\Note.md"));
        Assert.Equal([Elevated], policy.MutationRequires("Areas/./Agentic/Deep/../Note.md"));
    }

    [Fact]
    public void ContributionsUnionAcrossEveryMatchingEntry()
    {
        // Entries only add op classes; none can clear one another entry contributed.
        var policy = Policy($$"""
            [
              { "path": "Areas", "mutation_requires": "{{Content}}" },
              { "path": "Areas/Agentic", "mutation_requires": "{{Elevated}}" }
            ]
            """);

        Assert.Equal([Elevated, Content], policy.MutationRequires("Areas/Agentic/Note.md").OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void AStructuralRuleMatchesTheShapeAPathHas()
    {
        var policy = Policy($$"""[ { "rule": "folder_note", "mutation_requires": "{{Elevated}}" } ]""");

        Assert.Equal([Elevated], policy.MutationRequires("Projects/Widget/Widget.md"));
        Assert.Empty(policy.MutationRequires("Projects/Widget/Other.md"));
        Assert.Empty(policy.MutationRequires("Widget.md"));
    }

    [Fact]
    public void APolicyThatDeclaresNothingRequiresNothing()
    {
        Assert.Empty(PathPolicy.None.MutationRequires("Areas/Agentic/Note.md"));
    }

    /// <summary>
    /// Checks that path entries and structural rules can be queried separately. A relocation needs
    /// them apart, because a path entry applies wherever the move lands while a structural rule
    /// applies only where the path's structural role changes. Every other caller asks for both,
    /// which is the default.
    /// </summary>
    [Fact]
    public void TheTwoTargetKindsAreAskableApart()
    {
        var policy = Policy($$"""
            [
              { "path": "Areas/Agentic", "mutation_requires": "{{Elevated}}" },
              { "rule": "folder_note", "mutation_requires": "{{Elevated}}" }
            ]
            """);

        Assert.Equal([Elevated], policy.MutationRequires("Areas/Agentic/Note.md", PolicyTargets.Paths));
        Assert.Empty(policy.MutationRequires("Areas/Agentic/Note.md", PolicyTargets.Structural));

        Assert.Empty(policy.MutationRequires("Projects/Widget/Widget.md", PolicyTargets.Paths));
        Assert.Equal([Elevated], policy.MutationRequires("Projects/Widget/Widget.md", PolicyTargets.Structural));

        // The default is both, which is what a write asks and what every existing caller means.
        Assert.Equal([Elevated], policy.MutationRequires("Areas/Agentic/Agentic.md"));
    }

    [Fact]
    public void APathThePolicyCannotReadIsNotOneItSilentlyAdmits()
    {
        var policy = Policy($$"""[ { "path": "Areas", "mutation_requires": "{{Elevated}}" } ]""");

        Assert.Throws<ArgumentException>(() => policy.MutationRequires("../Escaping.md"));
    }
}
