using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The one pass scope does not bound. What is asserted is the pair of properties the seam exists
/// for: the pass really does reach notes the grant can neither read nor write, and it is reachable
/// only by a call that says so.
///
/// The grant here is scoped to one folder, and everything interesting is outside it.
/// </summary>
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
        // Without this the cases below would prove nothing: they need a note the grant genuinely
        // cannot reach.
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
        // The one narrowing the pass keeps: no path under a dot-directory is reachable by the tool
        // set at all, which is not something a scope imposed.
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
        // There is no creating form on the pass: a note is brought into existence by an operation
        // whose own target is scope-checked normally.
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

    // ---- The seam ----

    [Fact]
    public void ThePassIsObtainedOnlyByTheCallThatNamesIt()
    {
        // Bypassing scope has to be a deliberate act with one grep behind it, which holds only
        // while nothing else can construct the object.
        Assert.Empty(typeof(VaultWidePass).GetConstructors());
    }
}
