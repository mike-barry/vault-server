using System.Text.Json;
using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <see cref="VaultAccess"/>, the only producer of an admitted path: what a caller may name,
/// which refusal it gets, the difference an always-readable root makes between reading and
/// writing, and the silent filter used for enumeration.
/// </summary>
/// <remarks>
/// The vault these tests run against declares <c>Resources/Vault/Guides</c> always-readable and is
/// an empty directory. No test needs a file on disk, because admission never looks at one.
/// </remarks>
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
    public void ForWrite_AdmitsTheGrantsRootFolderItselfAndNotOnlyWhatIsUnderIt()
    {
        // A root names the folder as well as its contents, so listing that folder, creating a note
        // directly in it, and removing it are all in scope.
        Assert.Equal("Projects/Widget", Admitted(Narrow.ForWrite("Projects/Widget")).Value);
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

    // ---- Reading and writing an always-readable root ----

    [Fact]
    public void ForRead_ReachesAnAlwaysReadableRootOutsideTheGrantsRoots()
    {
        // A caller under any grant reaches the material it is instructed to obey.
        Assert.Equal(Guide, Admitted(Narrow.ForRead(Guide)).Value);
    }

    [Fact]
    public void ForWrite_RefusesAnAlwaysReadableRootOutsideTheGrantsRoots()
    {
        // A write is checked against the grant's own roots and nothing wider, so a caller can read
        // that material without being able to author it.
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
    [InlineData("//server/share.md")]
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
        // Enumeration can report a dot path, but no caller may name one whatever its scope, so
        // the perimeter is checked before the scope check.
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
        // A refusal carries the path the caller wrote and nothing else: not whether the target
        // exists, not the grant's roots, not which check failed.
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

    // ---- Silent filtering, used by enumeration ----

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
        // An enumeration asked for dot entries still filters them by scope. The dot rule applies
        // only to a path a caller named, so this member does not apply it.
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
        // A grant with no scope entry for this vault is an upstream defect, so it throws rather
        // than being read as permission.
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scratch" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["scratch"] = VaultScope.Unscoped });

        Assert.Throws<InvalidOperationException>(() => new VaultAccess(_vault, grant));
    }

    /// <summary>
    /// Checks <c>Beneath</c>, which admits paths derived from an already-admitted folder rather
    /// than named by the caller. It widens nothing, because a scope root is a whole-segment prefix
    /// and everything under an admitted folder is admitted by that same root. It does admit dot
    /// paths, which a caller may not name, because a recursive removal really does destroy them.
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

    /// <summary>
    /// Checks that a path outside the folder throws rather than returning a refusal, because it is
    /// a defect at the call site rather than an answer about scope.
    /// </summary>
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

/// <summary>
/// Tests that the dot-path perimeter holds against a vault whose <c>path_policy</c> declares a
/// dot directory as always readable.
/// </summary>
/// <remarks>
/// No path under a dot directory is reachable by the tool set. The rule is fixed in code rather
/// than configured, so neither a policy entry naming such a directory nor an unscoped grant can
/// open it. Policy and scope are combined with AND, and scope only narrows, so nothing widens the
/// perimeter.
/// </remarks>
public sealed class VaultAccessDeclaredPerimeterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-vault-access-perimeter-").FullName;
    private readonly Vault _vault;

    public VaultAccessDeclaredPerimeterTests()
    {
        var root = Path.Combine(_tempDir, "test");
        Directory.CreateDirectory(Path.Combine(root, ".obsidian"));
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            {
              "default_vault": "test",
              "vaults": {
                "test": {
                  "path": {{JsonSerializer.Serialize(root)}},
                  "path_policy": [ { "path": ".obsidian", "always_readable": true } ]
                }
              }
            }
            """);
        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, new RecordingProblemSink());
        _vault = Assert.IsType<VaultResolution.Resolved>(Vault.Resolve(config, "test")).Vault;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private VaultAccess Access(VaultScope scope) =>
        new(_vault, new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = scope }));

    [Fact]
    public void ADeclaredDotTargetStillRefusesAtThePerimeter()
    {
        Assert.IsType<PathResolution<ReadPath>.Excluded>(Access(VaultScope.Unscoped).ForRead(".obsidian/Notes.md"));
        Assert.IsType<PathResolution<ReadPath>.Excluded>(Access(VaultScope.Of([".obsidian"])).ForRead(".obsidian/Notes.md"));
    }

    /// <summary>
    /// Checks that the refusal comes from the perimeter rather than the scope check. The
    /// distinction matters to a caller, because no change to a grant remedies an exclusion.
    /// </summary>
    [Fact]
    public void TheRefusalNamesThePerimeterRatherThanScope()
    {
        var refusal = Assert.IsType<PathResolution<WritePath>.Excluded>(Access(VaultScope.Unscoped).ForWrite(".obsidian/Notes.md"));

        Assert.Equal(".obsidian/Notes.md", refusal.Path);
    }
}
