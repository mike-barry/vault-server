using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <see cref="VaultWidePass"/>, the one surface a grant's scope does not bound.
/// </summary>
/// <remarks>
/// Two properties are under test: the pass really does reach notes the grant can neither read nor
/// write, and it can be obtained only by calling <c>BypassingScope</c>. The grant here is scoped
/// to one folder and everything the tests use is outside it.
/// </remarks>
public sealed class VaultWidePassTests : IDisposable
{
    private readonly ScratchVault _vault = new(VaultScope.Of(["Projects"]));

    public VaultWidePassTests() => _vault
        .WithNote("Projects/Widget.md", "Inside the grant's scope.\n")
        .WithNote("Notes/Alpha.md", "See [[Widget]].\n")
        .WithNote("Archive/Deep/Beta.md", "See [[Widget]] too.\n")
        .WithNote("Notes/Data.txt", "Not a note.\n")
        .WithNote(".obsidian/Copy.md", "A vault copy.\n");

    public void Dispose() => _vault.Dispose();

    private VaultPath At(string relativePath) =>
        VaultPath.TryCreate(_vault.Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    private static IReadOnlyList<string> Values(SearchOutcome<VaultPath> outcome) =>
        [.. Assert.IsType<SearchOutcome<VaultPath>.Results>(outcome).Items.Select(p => p.Value).Order()];

    // ---- The grant really is narrow ----

    [Fact]
    public void TheScopedSurfacesWithholdWhatThePassReaches()
    {
        // The tests below need a note the grant genuinely cannot reach. Without this one they
        // would prove nothing.
        Assert.Equal(["Projects/Widget.md"], Values(_vault.Access.EnumerateNotes()));
        Assert.IsType<PathResolution<ReadPath>.OutOfScope>(_vault.Access.ForRead("Notes/Alpha.md"));
        Assert.IsType<PathResolution<WritePath>.OutOfScope>(_vault.Access.ForWrite("Notes/Alpha.md"));
    }

    // ---- What the pass reaches ----

    [Fact]
    public void EnumerateNotes_ReachesEveryNoteWhateverTheGrantAdmits()
    {
        Assert.Equal(
            ["Archive/Deep/Beta.md", "Notes/Alpha.md", "Projects/Widget.md"],
            Values(_vault.Access.BypassingScope().EnumerateNotes()));
    }

    [Fact]
    public void EnumerateNotes_AnswersNotesRatherThanEveryFile()
    {
        Assert.DoesNotContain("Notes/Data.txt", Values(_vault.Access.BypassingScope().EnumerateNotes()));
    }

    [Fact]
    public void EnumerateNotes_LeavesOutDotEntries()
    {
        // The pass drops its scope check but keeps the dot rule, because no path under a dot
        // directory is reachable by the tool set at all.
        Assert.DoesNotContain(".obsidian/Copy.md", Values(_vault.Access.BypassingScope().EnumerateNotes()));
    }

    [Fact]
    public void Read_ReachesANoteTheGrantCannotRead()
    {
        var outcome = Assert.IsType<ReadOutcome.Content>(_vault.Access.BypassingScope().Read(At("Notes/Alpha.md")));

        Assert.Equal("See [[Widget]].\n", outcome.Text);
    }

    [Fact]
    public void Read_ClassifiesAFailureAsTheScopedReadDoes()
    {
        var outcome = Assert.IsType<ReadOutcome.NotFound>(_vault.Access.BypassingScope().Read(At("Notes/Missing.md")));

        Assert.Equal("Notes/Missing.md", outcome.Path);
    }

    [Fact]
    public void Replace_WritesANoteTheGrantCannotWrite()
    {
        Assert.IsType<WriteOutcome.Written>(
            _vault.Access.BypassingScope().Replace(At("Archive/Deep/Beta.md"), "See [[Renamed]] too.\n"));

        Assert.Equal("See [[Renamed]] too.\n", _vault.TextOf("Archive/Deep/Beta.md"));
    }

    [Fact]
    public void Replace_BringsNoNoteIntoExistence()
    {
        // The pass has no creating write. A note is only brought into existence by an operation
        // whose target goes through the normal scope check.
        Assert.IsType<WriteOutcome.NotFound>(
            _vault.Access.BypassingScope().Replace(At("Notes/Missing.md"), "# New\n"));

        Assert.False(_vault.HasFile("Notes/Missing.md"));
    }

    [Fact]
    public void APathFromAnotherVaultIsALayerDefect()
    {
        var elsewhere = new Vault("other", _vault.Root, PathPolicy.None);
        Assert.True(VaultPath.TryCreate(elsewhere, "Notes/Alpha.md", out var foreign, out _));

        Assert.Throws<ArgumentException>(() => _vault.Access.BypassingScope().Read(foreign));
    }

    // ---- How the pass is obtained ----

    [Fact]
    public void ThePassIsObtainedOnlyByTheCallThatNamesIt()
    {
        // With no public constructor, every place that bypasses scope is found by searching for
        // BypassingScope.
        Assert.Empty(typeof(VaultWidePass).GetConstructors());
    }
}
