using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Matching a path against a policy's <c>mutation_requires</c> entries - the one matcher every
/// enforcer shares. The frontmatter write path consults it beside the authority assertion, and the
/// tools that remove or relocate consult it at their own validate phases, so a second implementation
/// would be a second answer to the same question.
///
/// What a policy entry parses into is <c>PathPolicyTests</c>'s subject; what a match means is here.
/// </summary>
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
        // No entry can clear one another contributed.
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
    /// The two target kinds are askable apart, for the one caller that needs them apart: a
    /// relocation, where a path entry contributes wherever the move goes and a structural rule
    /// contributes only where artifact status changes. Every other caller asks for both, which is
    /// the default.
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
