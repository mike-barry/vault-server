using System.Text.Json;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// A vault's path policy: what survives load validation, what it reports, and the read-scope
/// union its <c>always_readable</c> modifier performs. Writes its own vaults.json over temp
/// directories rather than using the committed fixture, because most of these cases turn on a
/// declared target existing or not existing on disk.
/// </summary>
public sealed class PathPolicyTests : IDisposable
{
    private const string Guides = "Resources/Vault/Guides";

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-path-policy-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Writes a vaults.json for one vault rooted at a fresh temp directory, creating
    /// <paramref name="contents"/> inside it (".md" entries become files, everything else a
    /// directory) and giving it <paramref name="policyJson"/> as its path_policy.</summary>
    private VaultConfig Load(RecordingProblemSink healthState, string? policyJson, params string[] contents)
    {
        var root = Path.Combine(_tempDir, "test");
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

        var policy = policyJson is null ? "" : $""", "path_policy": {policyJson}""";
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            { "default_vault": "test", "vaults": { "test": { "path": {{JsonSerializer.Serialize(root)}}{{policy}} } } }
            """);
        return VaultConfig.Load(configPath, healthState);
    }

    private static string[] Problems(RecordingProblemSink healthState) =>
        healthState.Problems.Select(p => p.Detail).ToArray();

    private static VaultScope Rooted(params string[] roots) =>
        VaultScope.Of(roots.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ---- Entry validation ----

    [Fact]
    public void Load_CanonicalizesTargetAndReportsNothingWhenItExists()
    {
        var healthState = new RecordingProblemSink();

        var config = Load(healthState, """[ { "path": "Resources\\Vault\\.\\Guides", "always_readable": true } ]""", Guides);

        Assert.Equal<string[]>([Guides], [.. config.PathPolicyFor("test").AlwaysReadableRoots]);
        Assert.Empty(Problems(healthState));
    }

    [Fact]
    public void Load_NoPolicyDeclaredIsNoFloor()
    {
        var healthState = new RecordingProblemSink();

        var config = Load(healthState, policyJson: null);

        Assert.Same(PathPolicy.None, config.PathPolicyFor("test"));
        Assert.Empty(Problems(healthState));
    }

    [Theory]
    [InlineData("""{ "path": "C:/elsewhere", "always_readable": true }""", "rooted target")]
    [InlineData("""{ "path": "../outside", "always_readable": true }""", "escaping target")]
    [InlineData("""{ "path": "", "always_readable": true }""", "empty target")]
    [InlineData("""{ "always_readable": true }""", "no target")]
    [InlineData("""{ "path": "Areas" }""", "no modifier")]
    [InlineData("""{ "path": "Areas", "always_readable": false }""", "modifier present but false")]
    [InlineData("""{ "path": "Areas", "always_redable": true }""", "misspelled modifier")]
    [InlineData("\"Areas\"", "not an object")]
    public void Load_MalformedEntryIsDroppedAndReported(string entryJson, string why)
    {
        var healthState = new RecordingProblemSink();

        var config = Load(healthState, $$"""[ {{entryJson}}, { "path": "{{Guides}}", "always_readable": true } ]""", Guides);

        // The rest of the policy survives the bad entry, and the vault still loads.
        Assert.Equal<string[]>([Guides], [.. config.PathPolicyFor("test").AlwaysReadableRoots]);
        var problem = Assert.Single(Problems(healthState));
        Assert.True(problem.Contains("dropped"), $"{why}: expected a drop to be reported, got: {problem}");
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("mutation_requires")]
    public void Load_SpecifiedButUnimplementedKeyIsRefusedRatherThanIgnored(string key)
    {
        // Silently ignoring these would let config claim a path is protected while nothing
        // enforces it - a lie that reads as working.
        var healthState = new RecordingProblemSink();

        var config = Load(healthState, $$"""[ { "path": "Areas", "{{key}}": "agentic:write" } ]""");

        Assert.Same(PathPolicy.None, config.PathPolicyFor("test"));
        Assert.Contains(Problems(healthState), p => p.Contains(key) && p.Contains("not implemented yet"));
    }

    [Fact]
    public void Load_DeclaredTargetMissingFromDiskIsReportedButKept()
    {
        var healthState = new RecordingProblemSink();

        var config = Load(healthState, $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""");

        Assert.Equal<string[]>([Guides], [.. config.PathPolicyFor("test").AlwaysReadableRoots]);
        Assert.Contains(Problems(healthState), p => p.Contains("does not exist"));
    }

    [Fact]
    public void Load_FileTargetIsValid()
    {
        var healthState = new RecordingProblemSink();

        var config = Load(healthState, """[ { "path": "Vault-Manifest.md", "always_readable": true } ]""", "Vault-Manifest.md");

        Assert.Equal<string[]>(["Vault-Manifest.md"], [.. config.PathPolicyFor("test").AlwaysReadableRoots]);
        Assert.Empty(Problems(healthState));
    }

    [Fact]
    public void Load_PolicyIsPerVaultAndAnotherVaultIsUnaffected()
    {
        // The property the flat, every-vault shape could not have: a vault that keeps no such
        // material declares none, and nothing is checked against it or reported for it.
        var healthState = new RecordingProblemSink();
        var withGuides = Path.Combine(_tempDir, "primary");
        var without = Path.Combine(_tempDir, "scratch");
        Directory.CreateDirectory(Path.Combine(withGuides, "Resources", "Vault", "Guides"));
        Directory.CreateDirectory(without);
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            {
              "default_vault": "primary",
              "vaults": {
                "primary": { "path": {{JsonSerializer.Serialize(withGuides)}}, "path_policy": [ { "path": "{{Guides}}", "always_readable": true } ] },
                "scratch": { "path": {{JsonSerializer.Serialize(without)}} }
              }
            }
            """);

        var config = VaultConfig.Load(configPath, healthState);

        Assert.Equal<string[]>([Guides], [.. config.PathPolicyFor("primary").AlwaysReadableRoots]);
        Assert.Same(PathPolicy.None, config.PathPolicyFor("scratch"));
        Assert.Empty(Problems(healthState));
    }

    // ---- Vault entry shape ----

    [Fact]
    public void Load_BareStringVaultEntryIsFatal()
    {
        var healthState = new RecordingProblemSink();
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""{ "default_vault": "test", "vaults": { "test": {{JsonSerializer.Serialize(_tempDir)}} } }""");

        var config = VaultConfig.Load(configPath, healthState);

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Empty(config.Vaults);
        Assert.Contains(Problems(healthState), p => p.Contains("must be an object"));
    }

    [Theory]
    [InlineData("frontmatter_registry", "not implemented yet")]
    [InlineData("always_readable", "unrecognized key")]
    public void Load_BadVaultEntryKeyIsFatal(string key, string expectedFragment)
    {
        // Unrecognized at the vault level rather than dropped: unlike a path_policy entry there
        // is no rest-of-the-list to preserve, and a vault carrying a key nobody reads is a
        // configuration someone believes is in effect.
        var healthState = new RecordingProblemSink();
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            { "default_vault": "test", "vaults": { "test": { "path": {{JsonSerializer.Serialize(_tempDir)}}, "{{key}}": "x" } } }
            """);

        var config = VaultConfig.Load(configPath, healthState);

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Empty(config.Vaults);
        Assert.Contains(Problems(healthState), p => p.Contains(expectedFragment));
    }

    // ---- The union ----

    [Fact]
    public void EffectiveReadScope_UnionsTheFloorIntoARootedGrantScope()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).PathPolicyFor("test");

        var effective = Assert.IsType<VaultScope.RootedScope>(policy.EffectiveReadScope(Rooted("Projects/Widget")));

        Assert.True(effective.Roots.SetEquals(["Projects/Widget", Guides]));
    }

    [Fact]
    public void EffectiveReadScope_LeavesAnUnscopedGrantUnscoped()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).PathPolicyFor("test");

        Assert.Same(VaultScope.Unscoped, policy.EffectiveReadScope(VaultScope.Unscoped));
    }

    [Fact]
    public void EffectiveReadScope_WithNoPolicyIsTheIdentity()
    {
        var scope = Rooted("Projects/Widget");

        Assert.Same(scope, PathPolicy.None.EffectiveReadScope(scope));
    }

    [Fact]
    public void IsPathInReadScope_NarrowGrantReachesTheFloorAndKeepsItsOwnRoots()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).PathPolicyFor("test");
        var scope = Rooted("Projects/Widget");

        Assert.True(VaultAccess.IsPathInReadScope($"{Guides}/Vault-Guide.md", scope, policy));
        Assert.True(VaultAccess.IsPathInReadScope("Projects/Widget/Widget.md", scope, policy));
        Assert.False(VaultAccess.IsPathInReadScope("Projects/Other/Other.md", scope, policy));
    }

    [Fact]
    public void IsPathInReadScope_MatchesTheFloorBySegmentLikeAnyOtherRoot()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).PathPolicyFor("test");

        Assert.False(VaultAccess.IsPathInReadScope("Resources/Vault/Guides-Old/Vault-Guide.md", Rooted("Projects/Widget"), policy));
    }

    [Fact]
    public void IsPathInReadScope_WithNoPolicyIsExactlyIsPathInScope()
    {
        Assert.False(VaultAccess.IsPathInReadScope($"{Guides}/Vault-Guide.md", Rooted("Projects/Widget"), PathPolicy.None));
    }

    /// <summary>
    /// The write path is untouched by the floor. This is the property that makes the floor safe:
    /// every agent reads the material it is instructed to obey, and none of them can author it.
    /// </summary>
    [Fact]
    public void IsPathInScope_TheWritePathDoesNotSeeTheFloor()
    {
        Assert.False(VaultAccess.IsPathInScope($"{Guides}/Vault-Guide.md", Rooted("Projects/Widget")));
    }

    /// <summary>
    /// Attenuation cannot remove the floor. A narrowed scope - the shape a minted token carries -
    /// still reaches the guides, because the union happens at check time rather than being copied
    /// into a grant's own roots where a later mint could drop it.
    /// </summary>
    [Fact]
    public void IsPathInReadScope_AttenuatedScopeStillReachesTheFloor()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).PathPolicyFor("test");

        Assert.True(VaultAccess.IsPathInReadScope($"{Guides}/Document-Conventions.md", Rooted("Projects/Widget/Inbox"), policy));
    }
}

/// <summary>
/// The floor against the committed fixture: the declared target names a folder that really exists
/// in <c>test-vault/</c>, in both of the fixture's vaults, and the files under it are real. Keeps
/// the repo's own vaults.json honest - a target pointed at a folder nobody seeded would pass every
/// case above and still be wrong.
/// </summary>
public sealed class PathPolicyFixtureTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    private const string Guides = "Resources/Vault/Guides";
    private const string Guide = $"{Guides}/Vault-Guide.md";

    private static readonly VaultScope Narrow = VaultScope.Of(new HashSet<string>(["Projects/Widget"], StringComparer.OrdinalIgnoreCase));

    /// <summary>Parses the repo's own policy against one of the fixture's vaults, asserting it
    /// reports nothing - which is only true if that vault really carries the folder.</summary>
    private static PathPolicy GuidesPolicy(VaultConfig config, string vault)
    {
        var healthState = new RecordingProblemSink();
        using var doc = JsonDocument.Parse($$"""[ { "path": "{{Guides}}", "always_readable": true } ]""");
        var policy = PathPolicy.Parse(doc.RootElement, vault, config.Resolve(vault).Root, "vaults.json", healthState);
        Assert.Empty(healthState.Problems);
        return policy;
    }

    [Fact]
    public void TheGuidesFolderExistsInEveryFixtureVault()
    {
        GuidesPolicy(fixture.Config, "test");
        GuidesPolicy(fixture.Config, "scratch");
    }

    [Fact]
    public void ANarrowGrantReadsTheSeededGuidesAndNothingElseOutsideItsRoots()
    {
        var policy = GuidesPolicy(fixture.Config, "test");

        Assert.True(VaultAccess.IsPathInReadScope(Guide, Narrow, policy));
        Assert.True(VaultAccess.IsPathInReadScope($"{Guides}/Document-Conventions.md", Narrow, policy));
        Assert.True(VaultAccess.IsPathInReadScope("Projects/Widget/Widget.md", Narrow, policy));
        Assert.False(VaultAccess.IsPathInReadScope("Resources/Gizmo-Reference.md", Narrow, policy));
        Assert.False(VaultAccess.IsPathInReadScope("Inbox/Note-Loose-Thought.md", Narrow, policy));
    }

    [Fact]
    public void ANarrowGrantCannotWriteTheSeededGuides()
    {
        Assert.False(VaultAccess.IsPathInScope(Guide, Narrow));
    }

    [Fact]
    public void TheSeededGuidesResolveToRealFilesUnderTheFixtureVault()
    {
        Assert.True(File.Exists(Path.Combine(fixture.Config.Resolve(null).Root, Guide)));
        Assert.True(File.Exists(Path.Combine(fixture.Config.Resolve("scratch").Root, Guide)));
    }
}
