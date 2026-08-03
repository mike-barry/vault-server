using System.Text.Json;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The union a read-class caller runs against: a grant's own scope in a vault widened by that
/// vault's always-readable roots. <c>VaultAccess</c> is what composes these two in production, and
/// composes them once per call rather than per path; here the pair is asserted directly, so a
/// change to the union is a failure in these cases rather than only in the layer's.
/// </summary>
internal static class ReadScopeExtensions
{
    internal static bool ReadAdmits(this PathPolicy policy, string relativePath, VaultScope grantScope) =>
        policy.EffectiveReadScope(grantScope).Admits(relativePath);
}

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
    private VaultConfig Load(RecordingProblemSink sink, string? policyJson, params string[] contents)
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
        return VaultConfigLoader.Load(configPath, TestOpClasses.Repo, sink);
    }

    private static string[] Problems(RecordingProblemSink sink) =>
        sink.Problems.Select(p => p.Detail).ToArray();

    private static VaultScope Rooted(params string[] roots) =>
        VaultScope.Of(roots.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ---- Entry validation ----

    [Fact]
    public void Load_CanonicalizesTargetAndReportsNothingWhenItExists()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, """[ { "path": "Resources\\Vault\\.\\Guides", "always_readable": true } ]""", Guides);

        Assert.Equal<string[]>([Guides], [.. config.Vaults["test"].PathPolicy.AlwaysReadableRoots]);
        Assert.Empty(Problems(sink));
    }

    [Fact]
    public void Load_NoPolicyDeclaredIsNoFloor()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, policyJson: null);

        Assert.Same(PathPolicy.None, config.Vaults["test"].PathPolicy);
        Assert.Empty(Problems(sink));
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
    [InlineData("""{ "rule": "not_a_rule", "mutation_requires": "content:write" }""", "unknown rule")]
    [InlineData("""{ "rule": "", "mutation_requires": "content:write" }""", "empty rule")]
    [InlineData("""{ "path": "Areas", "rule": "folder_note", "mutation_requires": "content:write" }""", "two targets")]
    [InlineData("""{ "path": "Areas", "mutation_requires": "" }""", "empty mutation_requires")]
    [InlineData("""{ "path": "Areas", "mutation_requires": "invented:class" }""", "undefined op class")]
    [InlineData("""{ "rule": "folder_note", "always_readable": true }""", "a rule cannot be a read root")]
    public void Load_MalformedEntryIsDroppedAndReported(string entryJson, string why)
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, $$"""[ {{entryJson}}, { "path": "{{Guides}}", "always_readable": true } ]""", Guides);

        // The rest of the policy survives the bad entry, and the vault still loads.
        Assert.Equal<string[]>([Guides], [.. config.Vaults["test"].PathPolicy.AlwaysReadableRoots]);
        var problem = Assert.Single(Problems(sink));
        Assert.True(problem.Contains("dropped"), $"{why}: expected a drop to be reported, got: {problem}");
    }

    // ---- mutation_requires ----

    [Fact]
    public void Load_CarriesAPathTargetsMutationRequires()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, """[ { "path": "Areas\\Agentic", "mutation_requires": "agentic:write" } ]""", "Areas/Agentic");

        var rule = Assert.Single(config.Vaults["test"].PathPolicy.MutationRules);
        Assert.Equal("Areas/Agentic", Assert.IsType<PathPolicy.PolicyTarget.Path>(rule.Target).Value);
        Assert.Equal("agentic:write", rule.RequiredOpClass);
        Assert.Empty(Problems(sink));
    }

    [Fact]
    public void Load_CarriesAStructuralRuleTarget()
    {
        // A shape a path has rather than a place it is, which a prefix target cannot express. The
        // rule is code; the declaration decides whether it applies and at what price.
        var sink = new RecordingProblemSink();

        var config = Load(sink, """[ { "rule": "folder_note", "mutation_requires": "content:write" } ]""");

        var rule = Assert.Single(config.Vaults["test"].PathPolicy.MutationRules);
        Assert.Equal(StructuralRule.FolderNote, Assert.IsType<PathPolicy.PolicyTarget.Structural>(rule.Target).Rule);
        Assert.Empty(Problems(sink));
    }

    [Fact]
    public void Load_OneEntryCarriesBothModifiers()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, $$"""[ { "path": "{{Guides}}", "always_readable": true, "mutation_requires": "agentic:write" } ]""", Guides);

        var policy = config.Vaults["test"].PathPolicy;
        Assert.Equal<string[]>([Guides], [.. policy.AlwaysReadableRoots]);
        Assert.Equal(Guides, Assert.IsType<PathPolicy.PolicyTarget.Path>(Assert.Single(policy.MutationRules).Target).Value);
    }

    [Fact]
    public void Load_MutationEntriesDoNotWidenTheReadFloor()
    {
        // Two modifiers, two effects. Naming a path's mutation price says nothing about who may
        // read it.
        var config = Load(new RecordingProblemSink(), """[ { "path": "Areas", "mutation_requires": "agentic:write" } ]""", "Areas");

        var policy = config.Vaults["test"].PathPolicy;
        Assert.Empty(policy.AlwaysReadableRoots);
        Assert.False(policy.ReadAdmits("Areas/Anything.md", Rooted("Projects/Widget")));
    }

    [Fact]
    public void Load_AnUndefinedOpClassDropsTheEntryAndSaysWhichClass()
    {
        // The same cross-file check grants and the frontmatter declaration run, for the same
        // reason: a policy naming an authority nothing defines is a protection nobody can satisfy
        // and nothing can enforce, which reads as working right up until it matters. Dropped rather
        // than fatal, like every other malformed entry - the rest of the policy still applies.
        var sink = new RecordingProblemSink();

        var config = Load(sink, $$"""
            [
              { "path": "Areas", "mutation_requires": "invented:class" },
              { "path": "{{Guides}}", "always_readable": true }
            ]
            """, Guides);

        var policy = config.Vaults["test"].PathPolicy;
        Assert.Empty(policy.MutationRules);
        Assert.Equal<string[]>([Guides], [.. policy.AlwaysReadableRoots]);
        Assert.Contains(Problems(sink), p => p.Contains("invented:class") && p.Contains("op-class configuration does not define"));
    }

    [Fact]
    public void Load_ADefinedOpClassIsCarriedInTheRegistrysOwnSpelling()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, """[ { "path": "Areas", "mutation_requires": "AGENTIC:write" } ]""", "Areas");

        Assert.Equal("agentic:write", Assert.Single(config.Vaults["test"].PathPolicy.MutationRules).RequiredOpClass);
        Assert.Empty(Problems(sink));
    }

    [Fact]
    public void Load_ContributionsFromSeveralEntriesAreAllKept()
    {
        // Contributions union - no entry can clear one another contributed - so every matching
        // entry has to survive the load for the union to be over all of them.
        var config = Load(new RecordingProblemSink(), """
            [
              { "path": "Areas", "mutation_requires": "agentic:write" },
              { "path": "Areas/Nested", "mutation_requires": "content:write" },
              { "rule": "folder_note", "mutation_requires": "content:write" }
            ]
            """, "Areas/Nested");

        Assert.Equal(3, config.Vaults["test"].PathPolicy.MutationRules.Count);
    }

    [Fact]
    public void Load_DeclaredTargetMissingFromDiskIsReportedButKept()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""");

        Assert.Equal<string[]>([Guides], [.. config.Vaults["test"].PathPolicy.AlwaysReadableRoots]);
        Assert.Contains(Problems(sink), p => p.Contains("does not exist"));
    }

    [Fact]
    public void Load_FileTargetIsValid()
    {
        var sink = new RecordingProblemSink();

        var config = Load(sink, """[ { "path": "Vault-Manifest.md", "always_readable": true } ]""", "Vault-Manifest.md");

        Assert.Equal<string[]>(["Vault-Manifest.md"], [.. config.Vaults["test"].PathPolicy.AlwaysReadableRoots]);
        Assert.Empty(Problems(sink));
    }

    [Fact]
    public void Load_PolicyIsPerVaultAndAnotherVaultIsUnaffected()
    {
        // The property the flat, every-vault shape could not have: a vault that keeps no such
        // material declares none, and nothing is checked against it or reported for it.
        var sink = new RecordingProblemSink();
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

        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, sink);

        Assert.Equal<string[]>([Guides], [.. config.Vaults["primary"].PathPolicy.AlwaysReadableRoots]);
        Assert.Same(PathPolicy.None, config.Vaults["scratch"].PathPolicy);
        Assert.Empty(Problems(sink));
    }

    // ---- Vault entry shape ----

    [Fact]
    public void Load_BareStringVaultEntryIsFatal()
    {
        var sink = new RecordingProblemSink();
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""{ "default_vault": "test", "vaults": { "test": {{JsonSerializer.Serialize(_tempDir)}} } }""");

        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, sink);

        Assert.True(sink.IsFatallyDegraded);
        Assert.Empty(config.Vaults);
        Assert.Contains(Problems(sink), p => p.Contains("must be an object"));
    }

    [Theory]
    [InlineData("always_readable", "unrecognized key")]
    [InlineData("frontmatter_regstry", "unrecognized key")]
    public void Load_BadVaultEntryKeyIsFatal(string key, string expectedFragment)
    {
        // Unrecognized at the vault level rather than dropped: unlike a path_policy entry there
        // is no rest-of-the-list to preserve, and a vault carrying a key nobody reads is a
        // configuration someone believes is in effect.
        var sink = new RecordingProblemSink();
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            { "default_vault": "test", "vaults": { "test": { "path": {{JsonSerializer.Serialize(_tempDir)}}, "{{key}}": "x" } } }
            """);

        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, sink);

        Assert.True(sink.IsFatallyDegraded);
        Assert.Empty(config.Vaults);
        Assert.Contains(Problems(sink), p => p.Contains(expectedFragment));
    }

    // ---- The union ----

    [Fact]
    public void EffectiveReadScope_UnionsTheFloorIntoARootedGrantScope()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).Vaults["test"].PathPolicy;

        var effective = Assert.IsType<VaultScope.RootedScope>(policy.EffectiveReadScope(Rooted("Projects/Widget")));

        Assert.True(effective.Roots.SetEquals(["Projects/Widget", Guides]));
    }

    [Fact]
    public void EffectiveReadScope_LeavesAnUnscopedGrantUnscoped()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).Vaults["test"].PathPolicy;

        Assert.Same(VaultScope.Unscoped, policy.EffectiveReadScope(VaultScope.Unscoped));
    }

    [Fact]
    public void EffectiveReadScope_WithNoPolicyIsTheIdentity()
    {
        var scope = Rooted("Projects/Widget");

        Assert.Same(scope, PathPolicy.None.EffectiveReadScope(scope));
    }

    [Fact]
    public void ReadAdmits_NarrowGrantReachesTheFloorAndKeepsItsOwnRoots()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).Vaults["test"].PathPolicy;
        var scope = Rooted("Projects/Widget");

        Assert.True(policy.ReadAdmits($"{Guides}/Vault-Guide.md", scope));
        Assert.True(policy.ReadAdmits("Projects/Widget/Widget.md", scope));
        Assert.False(policy.ReadAdmits("Projects/Other/Other.md", scope));
    }

    [Fact]
    public void ReadAdmits_MatchesTheFloorBySegmentLikeAnyOtherRoot()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).Vaults["test"].PathPolicy;

        Assert.False(policy.ReadAdmits("Resources/Vault/Guides-Old/Vault-Guide.md", Rooted("Projects/Widget")));
    }

    [Fact]
    public void ReadAdmits_WithNoPolicyIsExactlyTheScopeCheck()
    {
        Assert.False(PathPolicy.None.ReadAdmits($"{Guides}/Vault-Guide.md", Rooted("Projects/Widget")));
    }

    /// <summary>
    /// The write path is untouched by the floor. This is the property that makes the floor safe:
    /// every agent reads the material it is instructed to obey, and none of them can author it.
    /// </summary>
    [Fact]
    public void Admits_TheWritePathDoesNotSeeTheFloor()
    {
        Assert.False(Rooted("Projects/Widget").Admits($"{Guides}/Vault-Guide.md"));
    }

    /// <summary>
    /// Attenuation cannot remove the floor. A narrowed scope - the shape a minted token carries -
    /// still reaches the guides, because the union happens at check time rather than being copied
    /// into a grant's own roots where a later mint could drop it.
    /// </summary>
    [Fact]
    public void ReadAdmits_AttenuatedScopeStillReachesTheFloor()
    {
        var policy = Load(new RecordingProblemSink(), $$"""[ { "path": "{{Guides}}", "always_readable": true } ]""", Guides).Vaults["test"].PathPolicy;

        Assert.True(policy.ReadAdmits($"{Guides}/Document-Conventions.md", Rooted("Projects/Widget/Inbox")));
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
        var sink = new RecordingProblemSink();
        using var doc = JsonDocument.Parse($$"""[ { "path": "{{Guides}}", "always_readable": true } ]""");
        var policy = PathPolicyParser.Parse(doc.RootElement, vault, config.Vaults[vault].Root, "vaults.json", TestOpClasses.Repo, sink);
        Assert.Empty(sink.Problems);
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

        Assert.True(policy.ReadAdmits(Guide, Narrow));
        Assert.True(policy.ReadAdmits($"{Guides}/Document-Conventions.md", Narrow));
        Assert.True(policy.ReadAdmits("Projects/Widget/Widget.md", Narrow));
        Assert.False(policy.ReadAdmits("Resources/Gizmo-Reference.md", Narrow));
        Assert.False(policy.ReadAdmits("Inbox/Note-Loose-Thought.md", Narrow));
    }

    [Fact]
    public void ANarrowGrantCannotWriteTheSeededGuides()
    {
        Assert.False(Narrow.Admits(Guide));
    }

    [Fact]
    public void TheSeededGuidesResolveToRealFilesUnderTheFixtureVault()
    {
        Assert.True(File.Exists(Path.Combine(fixture.Config.Vaults["test"].Root, Guide)));
        Assert.True(File.Exists(Path.Combine(fixture.Config.Vaults["scratch"].Root, Guide)));
    }
}
