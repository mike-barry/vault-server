using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class VaultAccessTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    [Fact]
    public void EnumerateNotes_ReturnsFixtureNotesPlusInjectedDupes()
    {
        var notes = fixture.Vault.EnumerateNotes(null);
        Assert.Equal(8, notes.Count);
    }

    [Fact]
    public void EnumerateNotes_PrunesDotDirectories()
    {
        Assert.DoesNotContain(fixture.Vault.EnumerateNotes(null), n => n.Contains(".obsidian"));
    }

    [Fact]
    public void EnumerateNotes_ReturnsForwardSlashRelativePaths()
    {
        Assert.Contains("Projects/Widget/Widget.md", fixture.Vault.EnumerateNotes(null));
    }

    [Theory]
    [MemberData(nameof(WikilinkCases))]
    public void ResolveWikilink_SharedCases(string name, string link, string outcome, string? path)
    {
        switch (outcome)
        {
            case "resolves":
                var resolved = fixture.Vault.ResolveWikilink(null, link);
                Assert.True(path == resolved, $"{name}: expected {path}, got {resolved ?? "null"}");
                break;
            case "none":
                Assert.True(fixture.Vault.ResolveWikilink(null, link) is null, $"{name}: expected no match");
                break;
            case "ambiguous":
                Assert.Throws<InvalidOperationException>(() => fixture.Vault.ResolveWikilink(null, link));
                break;
            case "invalid":
                Assert.Throws<ArgumentException>(() => fixture.Vault.ResolveWikilink(null, link));
                break;
            default:
                Assert.Fail($"{name}: unknown outcome {outcome}");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(PathCases))]
    public void ResolvePath_SharedCases(string name, string path, bool valid)
    {
        if (valid)
        {
            var full = fixture.Vault.ResolvePath(null, path);
            Assert.True(Path.IsPathRooted(full), $"{name}: expected an absolute path, got {full}");
        }
        else
        {
            var ex = Record.Exception(() => fixture.Vault.ResolvePath(null, path));
            Assert.True(ex is ArgumentException, $"{name}: expected ArgumentException, got {ex?.GetType().Name ?? "no exception"}");
        }
    }

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public void IsPathInScope_SharedCases(string name, string path, List<string> scopeRoots, string outcome)
    {
        switch (outcome)
        {
            case "inScope":
                Assert.True(VaultAccess.IsPathInScope(path, scopeRoots), $"{name}: expected in scope");
                break;
            case "outOfScope":
                Assert.False(VaultAccess.IsPathInScope(path, scopeRoots), $"{name}: expected out of scope");
                break;
            case "invalid":
                Assert.Throws<ArgumentException>(() => VaultAccess.IsPathInScope(path, scopeRoots));
                break;
            default:
                Assert.Fail($"{name}: unknown outcome {outcome}");
                break;
        }
    }

    [Fact]
    public void EnumerateNotes_ScopedGrant_FiltersToRootFolder()
    {
        var notes = fixture.Vault.EnumerateNotes(null, ["Projects/Widget"]);
        Assert.All(notes, n => Assert.StartsWith("Projects/Widget/", n, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Projects/Widget/Widget.md", notes);
    }

    [Fact]
    public void EnumerateNotes_ScopedGrant_DropsOutOfScopeSiblingNotesRatherThanErroring()
    {
        var notes = fixture.Vault.EnumerateNotes(null, ["Projects/Widget"]);
        Assert.DoesNotContain("Resources/Gizmo-Reference.md", notes);
    }

    [Fact]
    public void ReadNote_InScopePath_Succeeds()
    {
        Assert.Contains("quorvium", fixture.Vault.ReadNote(null, "Projects/Widget/Tasks.md", ["Projects/Widget"]));
    }

    [Fact]
    public void ReadNote_OutOfScopePath_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Vault.ReadNote(null, "Resources/Gizmo-Reference.md", ["Projects/Widget"]));
    }

    [Fact]
    public void WriteNote_OutOfScopePath_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.Vault.WriteNote("scratch", "Areas/Note-Out-Of-Scope.md", "# Nope\n", ["Projects/Widget"]));
    }

    [Fact]
    public void MoveNote_OutOfScopeDestination_Throws()
    {
        fixture.Vault.WriteNote("scratch", "Projects/Widget/Note-Scoped-Move-Source.md", "# Source\n");
        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.Vault.MoveNote("scratch", "Projects/Widget/Note-Scoped-Move-Source.md", "Areas/Note-Scoped-Move-Dest.md", ["Projects/Widget"]));
    }

    [Fact]
    public void ResolveWikilink_ScopedGrant_DoesNotResolveAnOutOfScopeNote()
    {
        Assert.Null(fixture.Vault.ResolveWikilink(null, "Gizmo-Reference", ["Projects/Widget"]));
    }

    [Fact]
    public void ReadNote_ReturnsContent()
    {
        Assert.Contains("quorvium", fixture.Vault.ReadNote(null, "Resources/Gizmo-Reference.md"));
    }

    [Fact]
    public void ReadNote_MissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => fixture.Vault.ReadNote(null, "Missing.md"));
    }

    [Fact]
    public void WriteNote_WritesUtf8WithoutBom()
    {
        fixture.Vault.WriteNote("scratch", "Inbox/Note-Xunit-Written.md", "# Written\n\nquorvium check.\n");
        var bytes = File.ReadAllBytes(fixture.Vault.ResolvePath("scratch", "Inbox/Note-Xunit-Written.md"));
        Assert.True(bytes.Length > 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "unexpected UTF-8 BOM");
    }

    [Fact]
    public void WriteNote_CreatesParentDirectories()
    {
        fixture.Vault.WriteNote("scratch", "New-Folder/Nested/Note-Deep.md", "# Deep\n");
        Assert.True(File.Exists(fixture.Vault.ResolvePath("scratch", "New-Folder/Nested/Note-Deep.md")));
    }

    [Fact]
    public void MoveNote_MovesFileToNewPath()
    {
        fixture.Vault.WriteNote("scratch", "Inbox/Note-Move-Source.md", "# Source\n\nquorvium check.\n");
        fixture.Vault.MoveNote("scratch", "Inbox/Note-Move-Source.md", "Inbox/Note-Move-Dest.md");

        Assert.False(File.Exists(fixture.Vault.ResolvePath("scratch", "Inbox/Note-Move-Source.md")));
        Assert.True(File.Exists(fixture.Vault.ResolvePath("scratch", "Inbox/Note-Move-Dest.md")));
        Assert.Contains("quorvium", fixture.Vault.ReadNote("scratch", "Inbox/Note-Move-Dest.md"));
    }

    [Fact]
    public void MoveNote_CreatesParentDirectories()
    {
        fixture.Vault.WriteNote("scratch", "Note-Move-Nested-Source.md", "# Source\n");
        fixture.Vault.MoveNote("scratch", "Note-Move-Nested-Source.md", "New-Folder/Nested/Note-Move-Nested-Dest.md");

        Assert.True(File.Exists(fixture.Vault.ResolvePath("scratch", "New-Folder/Nested/Note-Move-Nested-Dest.md")));
    }

    [Fact]
    public void MoveNote_MissingSourceThrows()
    {
        Assert.Throws<FileNotFoundException>(() => fixture.Vault.MoveNote("scratch", "Missing-Source.md", "Missing-Dest.md"));
    }

    [Fact]
    public void MoveNote_ExistingDestinationThrows()
    {
        fixture.Vault.WriteNote("scratch", "Note-Move-Collide-Source.md", "# Source\n");
        fixture.Vault.WriteNote("scratch", "Note-Move-Collide-Dest.md", "# Dest\n");

        Assert.Throws<IOException>(() => fixture.Vault.MoveNote("scratch", "Note-Move-Collide-Source.md", "Note-Move-Collide-Dest.md"));
    }

    public static TheoryData<string, string, string, string?> WikilinkCases => SharedCases.Wikilink();
    public static TheoryData<string, string, bool> PathCases => SharedCases.Paths();
    public static TheoryData<string, string, List<string>, string> ScopeCases => SharedCases.Scope();
}
