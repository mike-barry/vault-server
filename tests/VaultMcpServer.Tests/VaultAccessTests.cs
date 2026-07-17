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

    public static TheoryData<string, string, string, string?> WikilinkCases => SharedCases.Wikilink();
    public static TheoryData<string, string, bool> PathCases => SharedCases.Paths();
}
