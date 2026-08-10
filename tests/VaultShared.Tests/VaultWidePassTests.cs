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
        // The set is exact, so it also pins what the pass leaves out. Notes/Data.txt is absent
        // because this surface answers notes rather than every file. .obsidian/Copy.md is absent
        // because the pass drops its scope check and keeps the dot rule. No path under a dot
        // directory is reachable by the tool set at all.
        Assert.Equal(
            ["Archive/Deep/Beta.md", "Notes/Alpha.md", "Projects/Widget.md"],
            Values(_vault.Access.BypassingScope().EnumerateNotes()));
    }

    [Fact]
    public void EnumerateFiles_ReachesEveryFileWhateverTheGrantAdmits()
    {
        // Notes/Data.txt is in the set because a folder relocation asks this surface for the
        // non-note files a directory rename carries, which is exactly what note enumeration cannot
        // see. .obsidian/Copy.md is out of it, as it is for notes.
        Assert.Equal(
            ["Archive/Deep/Beta.md", "Notes/Alpha.md", "Notes/Data.txt", "Projects/Widget.md"],
            Values(_vault.Access.BypassingScope().EnumerateFiles()));
    }

    [Fact]
    public void EnumerateFiles_LeavesOutDotEntries()
    {
        // The wider domain does not widen the dot rule. The destruction census admits dot entries
        // because it reports what it destroys; a repair rewrites link text, and no link may name a
        // path under a dot directory.
        _vault.WithNote(".obsidian/workspace.json", "{}\n");

        // The file is known to be on disk before the pass is asked whether it lists it, so a seeding
        // that produced nothing cannot satisfy the absence below.
        Assert.True(_vault.HasFile(".obsidian/workspace.json"));
        Assert.DoesNotContain(".obsidian/workspace.json", Values(_vault.Access.BypassingScope().EnumerateFiles()));
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
