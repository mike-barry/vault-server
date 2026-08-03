using System.Text.Json;
using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The one producer of an admitted path. The cases here are the layer's whole admission story: what
/// a caller may name, which of the three refusals it gets, the read-and-write asymmetry an
/// always-readable root creates, and the quiet filter that cannot refuse.
///
/// The vault it runs against declares <c>Resources/Vault/Guides</c> always-readable and exists only
/// as an empty directory - no case here needs a file, because admission never consults one.
/// </summary>
public sealed class VaultAccessTests : IDisposable
{
    private const string Guides = "Resources/Vault/Guides";
    private const string Guide = $"{Guides}/Vault-Guide.md";
    private const string InScope = "Projects/Widget/Widget.md";

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-vault-access-").FullName;
    private readonly Vault _vault;

    public VaultAccessTests()
    {
        var root = Path.Combine(_tempDir, "test");
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            {
              "default_vault": "test",
              "vaults": {
                "test": {
                  "path": {{JsonSerializer.Serialize(root)}},
                  "path_policy": [ { "path": "{{Guides}}", "always_readable": true } ]
                }
              }
            }
            """);
        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, new RecordingProblemSink());
        _vault = Assert.IsType<VaultResolution.Resolved>(Vault.Resolve(config, "test")).Vault;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private VaultAccess Access(VaultScope scope, params string[] opClasses) =>
        new(_vault, new Grant(
            "test-grant",
            new HashSet<string>(opClasses, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = scope }));

    private VaultAccess Narrow => Access(VaultScope.Of(["Projects/Widget"]));

    private static VaultPath Admitted<TPath>(PathResolution<TPath> resolution) where TPath : class =>
        Assert.IsType<PathResolution<TPath>.Admitted>(resolution).Path switch
        {
            ReadPath read => read.Path,
            WritePath write => write.Path,
            _ => throw new InvalidOperationException("Unknown admitted path type."),
        };

    // ---- Admission ----

    [Fact]
    public void ForRead_AdmitsAPathInsideTheGrantsOwnRoots()
    {
        Assert.Equal(InScope, Admitted(Narrow.ForRead(InScope)).Value);
    }

    [Fact]
    public void ForWrite_AdmitsAPathInsideTheGrantsOwnRoots()
    {
        Assert.Equal(InScope, Admitted(Narrow.ForWrite(InScope)).Value);
    }

    [Fact]
    public void AnAdmittedPathCarriesTheVaultItWasAdmittedIn()
    {
        Assert.Same(_vault, Admitted(Narrow.ForRead(InScope)).Vault);
    }

    [Fact]
    public void AsRead_WidensAWriteAndNothingNarrowsBack()
    {
        var write = Assert.IsType<PathResolution<WritePath>.Admitted>(Narrow.ForWrite(InScope)).Path;

        Assert.Equal(write.Path, write.AsRead().Path);
    }

    // ---- The read-and-write asymmetry ----

    [Fact]
    public void ForRead_ReachesAnAlwaysReadableRootOutsideTheGrantsRoots()
    {
        // The floor: an agent under any grant reaches the material it is instructed to obey.
        Assert.Equal(Guide, Admitted(Narrow.ForRead(Guide)).Value);
    }

    [Fact]
    public void ForWrite_RefusesAnAlwaysReadableRootOutsideTheGrantsRoots()
    {
        // The other half of the floor, and what makes it safe: every agent reads that material and
        // none of them can author it. A write is checked against the grant's own roots and nothing
        // wider.
        Assert.IsType<PathResolution<WritePath>.OutOfScope>(Narrow.ForWrite(Guide));
    }

    [Fact]
    public void AnUnscopedGrantIsUnaffectedByTheFloor()
    {
        var access = Access(VaultScope.Unscoped);

        Assert.Equal(Guide, Admitted(access.ForRead(Guide)).Value);
        Assert.Equal(Guide, Admitted(access.ForWrite(Guide)).Value);
    }

    [Fact]
    public void TheFloorMatchesBySegmentLikeAnyOtherRoot()
    {
        Assert.IsType<PathResolution<ReadPath>.OutOfScope>(Narrow.ForRead("Resources/Vault/Guides-Old/Vault-Guide.md"));
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/rooted.md")]
    [InlineData("")]
    [InlineData("Projects/..")]
    public void ForRead_MalformedTextIsItsOwnRefusal(string input)
    {
        var refusal = Assert.IsType<PathResolution<ReadPath>.Malformed>(Narrow.ForRead(input));

        Assert.Equal(input, refusal.Path);
        Assert.NotEmpty(refusal.Reason);
    }

    [Theory]
    [InlineData("Projects/Widget/.git/config")]
    [InlineData(".obsidian/workspace.json")]
    [InlineData("Projects/Widget/.gitkeep")]
    public void ForRead_ADotSegmentIsRefusedAtThePerimeterEvenInsideTheGrantsRoots(string input)
    {
        // Representable and not nameable: the census reports these, and no caller may name one
        // whatever its scope. Which is why the perimeter is decided ahead of the scope check.
        var refusal = Assert.IsType<PathResolution<ReadPath>.Excluded>(Narrow.ForRead(input));

        Assert.Equal(input, refusal.Path);
    }

    [Fact]
    public void ForWrite_ADotSegmentIsRefusedAtThePerimeterUnderAnUnscopedGrant()
    {
        Assert.IsType<PathResolution<WritePath>.Excluded>(Access(VaultScope.Unscoped).ForWrite(".git/config"));
    }

    [Fact]
    public void ARefusalCarriesTheCallersOwnCoordinatesAndNothingElse()
    {
        // Never whether the target exists, never the grant's roots, never the subset that failed.
        var refusal = Assert.IsType<PathResolution<ReadPath>.OutOfScope>(Narrow.ForRead("Inbox/Note-Loose-Thought.md"));

        Assert.Equal("Inbox/Note-Loose-Thought.md", refusal.Path);
        Assert.Equal("Path", Assert.Single(refusal.GetType().GetProperties()).Name);
    }

    [Fact]
    public void ARefusalIsTheSameWhetherOrNotTheTargetExists()
    {
        var absent = Narrow.ForRead("Inbox/Absent.md");
        Directory.CreateDirectory(Path.Combine(_vault.Root, "Inbox"));
        File.WriteAllText(Path.Combine(_vault.Root, "Inbox", "Present.md"), "# Present\n");

        Assert.IsType<PathResolution<ReadPath>.OutOfScope>(absent);
        Assert.IsType<PathResolution<ReadPath>.OutOfScope>(Narrow.ForRead("Inbox/Present.md"));
    }

    // ---- The quiet filter ----

    [Fact]
    public void Admits_FiltersAgainstTheSameEffectiveReadScopeForReadUses()
    {
        var access = Narrow;

        Assert.True(access.Admits(At(InScope)));
        Assert.True(access.Admits(At(Guide)));
        Assert.False(access.Admits(At("Inbox/Note-Loose-Thought.md")));
    }

    [Fact]
    public void Admits_DoesNotApplyThePerimeter()
    {
        // A census asked for dot entries still filters them by scope, so the dot rule is not this
        // member's to apply - it is a property of a path a caller named.
        Assert.True(Access(VaultScope.Unscoped).Admits(At(".obsidian/workspace.json")));
    }

    [Fact]
    public void Admits_RefusesACandidateFromAnotherVault()
    {
        var elsewhere = _vault with { Name = "scratch", Root = Path.Combine(_tempDir, "scratch") };
        Assert.True(VaultPath.TryCreate(elsewhere, InScope, out var foreign, out _));

        Assert.Throws<ArgumentException>(() => Narrow.Admits(foreign!));
    }

    // ---- The key assertion ----

    [Fact]
    public void AssertKey_PassesWhatTheGrantHoldsAndRefusesWhatItDoesNot()
    {
        var access = Access(VaultScope.Unscoped, "agentic:write");

        access.AssertKey("agentic:write");
        var refusal = Assert.Throws<AuthorityRefusal>(() => access.AssertKey("admin:write"));

        Assert.Equal("admin:write", refusal.OpClass);
        Assert.DoesNotContain("agentic:write", refusal.Message);
    }

    // ---- Construction ----

    [Fact]
    public void Construction_FailsOnAGrantWithNoScopeForThisVault()
    {
        // An upstream defect fails as one rather than being read as permission.
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scratch" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["scratch"] = VaultScope.Unscoped });

        Assert.Throws<InvalidOperationException>(() => new VaultAccess(_vault, grant));
    }

    /// <summary>
    /// The derived-target producer, for the one operation whose set is derived rather than named.
    /// It widens nothing - scope roots are whole-segment prefixes, so everything under an admitted
    /// folder is admitted by the same root - and it admits the dot paths the operand perimeter
    /// refuses a caller to name, because a recursive removal really does destroy them.
    /// </summary>
    [Fact]
    public void Beneath_AdmitsWhatLiesUnderAnAdmittedFolderIncludingADotEntry()
    {
        var access = Access(VaultScope.Of(["Projects"]));
        var folder = Assert.IsType<PathResolution<WritePath>.Admitted>(access.ForWrite("Projects/Widget")).Path;

        Assert.Equal("Projects/Widget/Notes.md", access.Beneath(folder, At("Projects/Widget/Notes.md")).Path.Value);
        Assert.Equal("Projects/Widget/.gitkeep", access.Beneath(folder, At("Projects/Widget/.gitkeep")).Path.Value);
        // The folder itself is in its own set.
        Assert.Equal("Projects/Widget", access.Beneath(folder, At("Projects/Widget")).Path.Value);
    }

    /// <summary>A path that is not under the folder is a defect at the call site rather than a scope
    /// answer, so it throws instead of refusing.</summary>
    [Fact]
    public void Beneath_RefusesAPathThatIsNotUnderTheFolder()
    {
        var access = Access(VaultScope.Unscoped);
        var folder = Assert.IsType<PathResolution<WritePath>.Admitted>(access.ForWrite("Projects/Widget")).Path;

        Assert.Throws<ArgumentException>(() => access.Beneath(folder, At("Projects/Widget-Two/Notes.md")));
        Assert.Throws<ArgumentException>(() => access.Beneath(folder, At("Reference/Guide.md")));
    }

    private VaultPath At(string relativePath)
    {
        Assert.True(VaultPath.TryCreate(_vault, relativePath, out var path, out var reason), reason);
        Assert.NotNull(path);
        return path;
    }
}
