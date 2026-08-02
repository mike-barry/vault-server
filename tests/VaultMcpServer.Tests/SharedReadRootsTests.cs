using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

/// <summary>
/// The read-class scope floor: what survives load validation, what it reports, and the union it
/// performs. Its own temp vaults rather than the committed fixture, because half of these cases
/// turn on a path existing or not existing on disk.
/// </summary>
public sealed class SharedReadRootsTests : IDisposable
{
    private const string Guides = "Resources/Vault/Guides";

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-shared-roots-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>A resolvable vault rooted at a fresh temp directory, with <paramref name="contents"/>
    /// created inside it (a trailing-slash-free relative path per entry; ".md" entries become files).</summary>
    private VaultConfig VaultWith(string name, params string[] contents)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(root);
        foreach (var relative in contents)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Path.GetExtension(full).Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
            }
            else
            {
                Directory.CreateDirectory(full);
            }
        }
        return new VaultConfig
        {
            Vaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = Path.GetFullPath(root) },
            DefaultVault = name,
        };
    }

    private static string[] ProblemsFrom(ServerHealthState healthState) =>
        healthState.Problems.Where(p => p.Source == "SharedReadRoots").Select(p => p.Detail).ToArray();

    private static VaultScope Rooted(params string[] roots) =>
        VaultScope.Of(roots.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ---- Load validation ----

    [Fact]
    public void Load_CanonicalizesEntryAndReportsNothingWhenItExists()
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load([@"Resources\Vault\.\Guides"], VaultWith("test", Guides), healthState);

        Assert.Equal<string[]>([Guides], [.. shared.Roots]);
        Assert.Empty(ProblemsFrom(healthState));
    }

    [Fact]
    public void Load_NullConfigurationIsNoFloor()
    {
        var healthState = new ServerHealthState();

        Assert.Same(SharedReadRoots.None, SharedReadRoots.Load(null, VaultWith("test"), healthState));
        Assert.Empty(ProblemsFrom(healthState));
    }

    [Theory]
    [InlineData("C:/Repos/elsewhere", "rooted")]
    [InlineData("../outside", "escaping")]
    [InlineData("   ", "blank")]
    [InlineData("", "empty")]
    public void Load_MalformedEntryIsDroppedAndReported(string entry, string why)
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load([entry, Guides], VaultWith("test", Guides), healthState);

        // The floor survives the bad entry - one malformed value does not cost the rest.
        Assert.Equal<string[]>([Guides], [.. shared.Roots]);
        var problem = Assert.Single(ProblemsFrom(healthState));
        Assert.True(problem.Contains("dropped"), $"{why} entry: expected a drop to be reported, got: {problem}");
    }

    [Fact]
    public void Load_EveryEntryMalformedLeavesNoFloor()
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load(["../outside", ""], VaultWith("test"), healthState);

        Assert.Same(SharedReadRoots.None, shared);
        Assert.Equal(2, ProblemsFrom(healthState).Length);
    }

    [Fact]
    public void Load_DeduplicatesEntriesThatCanonicalizeTheSame()
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load([Guides, @"Resources\Vault\Guides", "resources/vault/guides"], VaultWith("test", Guides), healthState);

        Assert.Equal<string[]>([Guides], [.. shared.Roots]);
        Assert.Empty(ProblemsFrom(healthState));
    }

    [Fact]
    public void Load_MissingPathInAResolvableVaultIsReportedButKept()
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load([Guides], VaultWith("test"), healthState);

        Assert.Equal<string[]>([Guides], [.. shared.Roots]);
        var problem = Assert.Single(ProblemsFrom(healthState));
        Assert.Contains("does not exist in vault \"test\"", problem);
    }

    [Fact]
    public void Load_FileEntryIsAValidRoot()
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load(["Vault-Manifest.md"], VaultWith("test", "Vault-Manifest.md"), healthState);

        Assert.Equal<string[]>(["Vault-Manifest.md"], [.. shared.Roots]);
        Assert.Empty(ProblemsFrom(healthState));
    }

    [Fact]
    public void Load_ChecksEveryResolvableVault()
    {
        var healthState = new ServerHealthState();
        var withGuides = VaultWith("primary", Guides);
        var withoutGuides = VaultWith("scratch");
        var both = new VaultConfig
        {
            Vaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["primary"] = withGuides.Vaults["primary"],
                ["scratch"] = withoutGuides.Vaults["scratch"],
            },
            DefaultVault = "primary",
        };

        SharedReadRoots.Load([Guides], both, healthState);

        var problem = Assert.Single(ProblemsFrom(healthState));
        Assert.Contains("vault \"scratch\"", problem);
    }

    [Fact]
    public void Load_SkippedVaultIsNotCheckedAndReportsNothing()
    {
        var healthState = new ServerHealthState();
        var config = new VaultConfig
        {
            Vaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DefaultVault = "broken",
            SkippedVaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken"] = "root directory does not exist: C:/nope",
            },
        };

        var shared = SharedReadRoots.Load([Guides], config, healthState);

        // The vault's own Degraded record already says the root is broken; one more per shared
        // root would report the same fact once per entry.
        Assert.Equal<string[]>([Guides], [.. shared.Roots]);
        Assert.Empty(ProblemsFrom(healthState));
    }

    // ---- The union ----

    [Fact]
    public void EffectiveReadScope_UnionsTheFloorIntoARootedGrantScope()
    {
        var shared = SharedReadRoots.Load([Guides], VaultWith("test", Guides), new ServerHealthState());

        var effective = Assert.IsType<VaultScope.RootedScope>(shared.EffectiveReadScope(Rooted("Projects/Widget")));

        Assert.True(effective.Roots.SetEquals(["Projects/Widget", Guides]));
    }

    [Fact]
    public void EffectiveReadScope_LeavesAnUnscopedGrantUnscoped()
    {
        var shared = SharedReadRoots.Load([Guides], VaultWith("test", Guides), new ServerHealthState());

        Assert.Same(VaultScope.Unscoped, shared.EffectiveReadScope(VaultScope.Unscoped));
    }

    [Fact]
    public void EffectiveReadScope_WithNoFloorIsTheIdentity()
    {
        var scope = Rooted("Projects/Widget");

        Assert.Same(scope, SharedReadRoots.None.EffectiveReadScope(scope));
    }

    [Fact]
    public void IsPathInReadScope_NarrowGrantReachesTheFloorAndKeepsItsOwnRoots()
    {
        var shared = SharedReadRoots.Load([Guides], VaultWith("test", Guides), new ServerHealthState());
        var scope = Rooted("Projects/Widget");

        Assert.True(VaultAccess.IsPathInReadScope($"{Guides}/Vault-Guide.md", scope, shared));
        Assert.True(VaultAccess.IsPathInReadScope("Projects/Widget/Widget.md", scope, shared));
        Assert.False(VaultAccess.IsPathInReadScope("Projects/Other/Other.md", scope, shared));
    }

    [Fact]
    public void IsPathInReadScope_MatchesTheFloorBySegmentLikeAnyOtherRoot()
    {
        var shared = SharedReadRoots.Load([Guides], VaultWith("test", Guides), new ServerHealthState());
        var scope = Rooted("Projects/Widget");

        Assert.False(VaultAccess.IsPathInReadScope("Resources/Vault/Guides-Old/Vault-Guide.md", scope, shared));
    }

    [Fact]
    public void IsPathInReadScope_WithNoFloorIsExactlyIsPathInScope()
    {
        var scope = Rooted("Projects/Widget");

        Assert.False(VaultAccess.IsPathInReadScope($"{Guides}/Vault-Guide.md", scope, SharedReadRoots.None));
    }

    /// <summary>
    /// The write path is untouched by the floor. This is the property that makes the floor safe:
    /// every agent reads the material it is instructed to obey, and none of them can author it.
    /// </summary>
    [Fact]
    public void IsPathInScope_TheWritePathDoesNotSeeTheFloor()
    {
        var scope = Rooted("Projects/Widget");

        Assert.False(VaultAccess.IsPathInScope($"{Guides}/Vault-Guide.md", scope));
    }

    /// <summary>
    /// Attenuation cannot remove the floor. A narrowed scope - the shape a minted token carries -
    /// still reaches the guides, because the union happens at check time rather than being copied
    /// into a grant's own roots where a later mint could drop it.
    /// </summary>
    [Fact]
    public void IsPathInReadScope_AttenuatedScopeStillReachesTheFloor()
    {
        var shared = SharedReadRoots.Load([Guides], VaultWith("test", Guides), new ServerHealthState());
        var attenuated = Rooted("Projects/Widget/Inbox");

        Assert.True(VaultAccess.IsPathInReadScope($"{Guides}/Document-Conventions.md", attenuated, shared));
    }
}

/// <summary>
/// The floor against the committed fixture rather than a hand-built temp vault: the configured
/// root names a folder that really exists in <c>test-vault/</c>, in both of the fixture's vaults,
/// and the files under it are real. This is what keeps the appsettings value honest - a shared
/// root pointed at a folder nobody seeded would pass every test above and still be wrong.
/// </summary>
public sealed class SharedReadRootsFixtureTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    private const string Guides = "Resources/Vault/Guides";
    private const string Guide = $"{Guides}/Vault-Guide.md";

    /// <summary>The scope of a grant confined to one project - the narrow grant the floor exists
    /// for.</summary>
    private static readonly VaultScope Narrow = VaultScope.Of(new HashSet<string>(["Projects/Widget"], StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Load_TheConfiguredRootExistsInEveryFixtureVault()
    {
        var healthState = new ServerHealthState();

        var shared = SharedReadRoots.Load([Guides], fixture.Config, healthState);

        Assert.Equal<string[]>([Guides], [.. shared.Roots]);
        // Both "test" and "scratch" are resolvable copies of the committed fixture, so a missing
        // folder would report twice. Silence here means the fixture really carries the folder.
        Assert.DoesNotContain(healthState.Problems, p => p.Source == "SharedReadRoots");
    }

    [Fact]
    public void ANarrowGrantReadsTheSeededGuidesAndNothingElseOutsideItsRoots()
    {
        var shared = SharedReadRoots.Load([Guides], fixture.Config, new ServerHealthState());

        Assert.True(VaultAccess.IsPathInReadScope(Guide, Narrow, shared));
        Assert.True(VaultAccess.IsPathInReadScope($"{Guides}/Document-Conventions.md", Narrow, shared));
        Assert.True(VaultAccess.IsPathInReadScope("Projects/Widget/Widget.md", Narrow, shared));
        Assert.False(VaultAccess.IsPathInReadScope("Resources/Gizmo-Reference.md", Narrow, shared));
        Assert.False(VaultAccess.IsPathInReadScope("Inbox/Note-Loose-Thought.md", Narrow, shared));
    }

    [Fact]
    public void ANarrowGrantCannotWriteTheSeededGuides()
    {
        Assert.False(VaultAccess.IsPathInScope(Guide, Narrow));
    }

    [Fact]
    public void TheSeededGuidesResolveToRealFilesUnderTheFixtureVault()
    {
        // The floor is only worth having if the paths it admits are readable: resolve through the
        // same chokepoint a read would and confirm the file is really there.
        Assert.True(File.Exists(fixture.Vault.ResolvePath(null, Guide)));
        Assert.True(File.Exists(fixture.Vault.ResolvePath("scratch", Guide)));
    }
}
