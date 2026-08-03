using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Containment as a property of the type: what text becomes a path, what it renders as, and how two
/// paths compare. Every case here runs against a root that does not exist, which is the point -
/// containment is decided textually and touches no filesystem.
/// </summary>
public sealed class VaultPathTests
{
    private static readonly Vault TestVault =
        new("test", Path.Combine(Path.GetTempPath(), "no-such-vault-" + Guid.NewGuid().ToString("N")), PathPolicy.None);

    private static VaultPath Create(string relativePath, Vault? vault = null)
    {
        Assert.True(VaultPath.TryCreate(vault ?? TestVault, relativePath, out var path, out var reason), reason);
        Assert.NotNull(path);
        return path;
    }

    [Fact]
    public void TryCreate_TouchesNoFilesystem()
    {
        // The vault root and everything under it are absent, and resolution answers anyway. This is
        // what makes the check cheap, applicable to every path, and identical for a path that does
        // not exist yet - which creation depends on.
        Assert.False(Directory.Exists(TestVault.Root));

        Assert.Equal("Projects/Widget/Widget.md", Create("Projects/Widget/Widget.md").Value);
    }

    [Theory]
    [InlineData(@"Projects\Widget\Widget.md")]
    [InlineData("Projects/Widget/Widget.md")]
    [InlineData("./Projects/Widget/Widget.md")]
    [InlineData("Projects/Gizmo/../Widget/Widget.md")]
    [InlineData("Projects//Widget/Widget.md")]
    public void TryCreate_RendersOneSeparatorConventionWhateverTheInput(string input)
    {
        // Emitted paths are quoted back into later calls, so every spelling of one path renders the
        // same way on every platform.
        var path = Create(input);

        Assert.Equal("Projects/Widget/Widget.md", path.Value);
        Assert.Equal("Projects/Widget/Widget.md", path.ToString());
        Assert.Equal<string[]>(["Projects", "Widget", "Widget.md"], [.. path.Segments]);
    }

    [Theory]
    [InlineData("../outside.md", "escaping")]
    [InlineData("Projects/../../outside.md", "escaping past the root after normalizing")]
    [InlineData("/Projects/Widget.md", "rooted")]
    [InlineData("", "empty")]
    [InlineData("   ", "blank")]
    [InlineData(".", "denoting the vault root")]
    [InlineData("Projects/..", "denoting the vault root after normalizing")]
    public void TryCreate_RefusesTextThatIsNotAContainedPath(string input, string why)
    {
        // Escaping text is refused, never clamped or normalized into something else. The root is not
        // a path either: a value with no segments would be admitted under every scope.
        Assert.False(VaultPath.TryCreate(TestVault, input, out var path, out var reason), why);
        Assert.Null(path);
        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
        Assert.DoesNotContain("(Parameter", reason);
    }

    [Fact]
    public void TryCreate_RefusesAnAbsolutePathOnThisPlatform()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere", "Widget.md");

        Assert.False(VaultPath.TryCreate(TestVault, rooted, out _, out _));
    }

    [Fact]
    public void TryCreate_CarriesTheVault()
    {
        Assert.Same(TestVault, Create("Widget.md").Vault);
    }

    [Theory]
    [InlineData(".obsidian/workspace.json")]
    [InlineData("Projects/.git/config")]
    [InlineData("Projects/Widget/.gitkeep")]
    public void TryCreate_DotSegmentsAreRepresentable(string input)
    {
        // The census reports what a removal destroys, including dot entries, so these must exist as
        // paths. Refusing to name one is the operand perimeter's job, not containment's.
        Assert.True(Create(input).HasDotSegment);
    }

    [Theory]
    [InlineData("Projects/Widget/Widget.md")]
    [InlineData("Projects/Widget/.hidden/Note.md")]
    public void HasDotSegment_AnswersForEverySegmentAtEveryDepth(string input)
    {
        Assert.Equal(input.Split('/').Any(s => s.StartsWith('.')), Create(input).HasDotSegment);
    }

    [Fact]
    public void Equality_IsCaseInsensitiveBecauseTwoSpellingsNameOneFile()
    {
        var path = Create("Projects/Widget/Widget.md");
        var other = Create("projects/widget/WIDGET.MD");

        Assert.Equal(path, other);
        Assert.Equal(path.GetHashCode(), other.GetHashCode());
        Assert.Single(new HashSet<VaultPath> { path, other });
    }

    [Fact]
    public void HasSameSpellingAs_IsOrdinalSoACaseOnlyRenameIsNotACollision()
    {
        var source = Create("Projects/Widget/widget.md");

        Assert.False(source.HasSameSpellingAs(Create("Projects/Widget/Widget.md")));
        Assert.True(source.HasSameSpellingAs(Create("Projects/Widget/widget.md")));
    }

    [Fact]
    public void Equality_TwoVaultsNeverAgree()
    {
        var elsewhere = TestVault with { Name = "scratch", Root = Path.Combine(Path.GetTempPath(), "other") };
        var path = Create("Widget.md");
        var other = Create("Widget.md", elsewhere);

        Assert.NotEqual(path, other);
        Assert.False(path.HasSameSpellingAs(other));
    }
}
