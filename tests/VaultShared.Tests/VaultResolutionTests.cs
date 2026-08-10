using System.Text.Json;
using VaultShared.Access;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests resolving a vault name against configuration: the spelling the result carries, and the
/// failures told apart by their type rather than by their message text.
/// </summary>
public sealed class VaultResolutionTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    private static Vault Resolved(VaultConfig config, string? name) =>
        Assert.IsType<VaultResolution.Resolved>(Vault.Resolve(config, name)).Vault;

    [Fact]
    public void Resolve_FallsBackToTheDefaultVault()
    {
        Assert.Equal("test", Resolved(fixture.Config, null).Name);
        Assert.Equal("test", Resolved(fixture.Config, "").Name);
        Assert.Equal("test", Resolved(fixture.Config, "   ").Name);
    }

    [Fact]
    public void Resolve_NamedVault()
    {
        Assert.Equal("scratch", Resolved(fixture.Config, "scratch").Name);
    }

    [Fact]
    public void Resolve_AnswersTheConfiguredSpellingNotTheCallersName()
    {
        // A caller quotes the resolved name back into later calls, so echoing "TEST" would make
        // the round trip lossy with nothing to detect it.
        Assert.Equal("test", Resolved(fixture.Config, "TEST").Name);
    }

    [Fact]
    public void Resolve_CarriesTheVaultsRootAndPolicy()
    {
        var vault = Resolved(fixture.Config, "test");

        Assert.Equal(fixture.Config.Vaults["test"].Root, vault.Root);
        Assert.Same(fixture.Config.Vaults["test"].PathPolicy, vault.Policy);
    }

    [Fact]
    public void Resolve_UnknownVaultAnswersRatherThanThrowing()
    {
        var unknown = Assert.IsType<VaultResolution.Unknown>(Vault.Resolve(fixture.Config, "nope"));

        Assert.Equal("nope", unknown.Name);
    }
}

/// <summary>
/// Tests resolution against a configuration that holds a skipped vault.
/// </summary>
/// <remarks>
/// A name nobody configured is a caller error, while a configured vault that would not load is an
/// operational problem. Resolution returns a different result type for each, so a caller tells
/// them apart without reading a message.
/// </remarks>
public sealed class VaultResolutionStateTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-vault-resolution-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private VaultConfig Load(string defaultVault)
    {
        var good = Path.Combine(_tempDir, "good");
        Directory.CreateDirectory(good);
        var path = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(path, $$"""
            {
              "default_vault": "{{defaultVault}}",
              "vaults": {
                "Good": { "path": {{JsonSerializer.Serialize(good)}} },
                "Broken": { "path": {{JsonSerializer.Serialize(Path.Combine(_tempDir, "no-such-dir"))}} }
              }
            }
            """);
        return VaultConfigLoader.Load(path, TestOpClasses.Repo, new RecordingProblemSink());
    }

    [Fact]
    public void Resolve_SkippedVaultIsUnavailableAndUnconfiguredIsUnknown()
    {
        var config = Load("Good");

        var unavailable = Assert.IsType<VaultResolution.Unavailable>(Vault.Resolve(config, "broken"));
        Assert.Equal("Broken", unavailable.Name);
        Assert.Contains("does not exist", unavailable.Reason);

        Assert.IsType<VaultResolution.Unknown>(Vault.Resolve(config, "nope"));
    }

    [Fact]
    public void Resolve_DefaultVaultThatWasSkippedIsUnavailable()
    {
        Assert.IsType<VaultResolution.Unavailable>(Vault.Resolve(Load("Broken"), null));
    }

    [Fact]
    public void Resolve_AgainstAConfigThatWouldNotLoadIsUnknown()
    {
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(Path.Combine(_tempDir, "absent.json"), TestOpClasses.Repo, sink);

        Assert.True(sink.IsFatallyDegraded);
        Assert.IsType<VaultResolution.Unknown>(Vault.Resolve(config, null));
        Assert.IsType<VaultResolution.Unknown>(Vault.Resolve(config, "Good"));
    }
}

/// <summary>
/// Tests how <c>Vault.LockName</c> is derived.
/// </summary>
/// <remarks>
/// The mutex name is a cross-process contract, so these tests pin every step of the derivation.
/// <c>Vault.LockName</c> carries what the contract is and what changing it breaks.
/// </remarks>
public sealed class VaultLockNameTests
{
    private static Vault At(string root) => new("test", root, PathPolicy.None);

    private const string Prefix = @"Global\VaultWrite-";

    [Fact]
    public void LockName_IsThePrefixAnd32UppercaseHexCharacters()
    {
        var name = At(Path.Combine(Path.GetTempPath(), "vault")).LockName;

        Assert.StartsWith(Prefix, name);
        var hash = name[Prefix.Length..];
        Assert.Equal(32, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789ABCDEF"));
    }

    [Fact]
    public void LockName_IsTheSha256OfTheLowercasedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Vault-Lock-Case");
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())))[..32];

        Assert.Equal(Prefix + expected, At(root).LockName);
    }

    [Fact]
    public void LockName_IgnoresCaseAndTrailingSeparatorsInTheConfiguredRoot()
    {
        // A case-only or trailing-slash difference between two configurations would otherwise give
        // two processes different locks over the same files.
        var root = Path.Combine(Path.GetTempPath(), "Vault-Lock-Name");

        Assert.Equal(At(root).LockName, At(root.ToUpperInvariant()).LockName);
        Assert.Equal(At(root).LockName, At(root + Path.DirectorySeparatorChar).LockName);
        Assert.Equal(At(root).LockName, At(root + "/").LockName);
    }

    [Fact]
    public void LockName_DerivesFromTheLocationNotTheName()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault");

        Assert.Equal(
            new Vault("one", root, PathPolicy.None).LockName,
            new Vault("two", root, PathPolicy.None).LockName);
    }

    [Fact]
    public void LockName_DiffersBetweenVaults()
    {
        Assert.NotEqual(
            At(Path.Combine(Path.GetTempPath(), "one")).LockName,
            At(Path.Combine(Path.GetTempPath(), "two")).LockName);
    }
}
