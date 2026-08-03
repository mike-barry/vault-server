using System.Text.Json;
using VaultCli;

namespace VaultCli.Tests;

/// <summary>What one invocation answered: the exit code, and each stream on its own.</summary>
/// <param name="Output">Standard output, which carries the answer and nothing else.</param>
/// <param name="Error">Standard error, which carries a refusal envelope, a parser message, or a
/// diagnostic.</param>
public sealed record CliRun(int Exit, string Output, string Error)
{
    /// <summary>The refusal envelope standard error carried, parsed. A run that refused writes
    /// exactly one.</summary>
    public JsonElement Envelope => JsonDocument.Parse(Error.Trim()).RootElement;

    /// <summary>The <c>code</c> from that envelope - what a caller branches on, whichever layer
    /// refused.</summary>
    public string Code => Envelope.GetProperty("code").GetString()!;
}

/// <summary>
/// A whole CLI deployment in a temp directory: a copy of the committed vault fixture, the three
/// configuration files, and the settings file naming them. Every case runs the shipped entry point
/// against it, so what is under test is the invocation an operator makes rather than a rearrangement
/// of its parts.
///
/// <b>Nothing here touches the committed fixture.</b> The vault is a copy, and the cases that reach
/// a mutating tool take its preview path, which runs the same validation and the same lock without
/// writing.
///
/// The grants are named rows: <c>cli-admin</c> is unrestricted, standing in for what the shipped
/// configuration supplies, and <c>cli-read</c> holds <c>content:read</c> alone, which is the narrow
/// grant a refusal is asserted under. Both are token-less, because that is the only kind of row a
/// grant id selects.
/// </summary>
public sealed class CliFixture : IDisposable
{
    public const string AdminGrant = "cli-admin";
    public const string ReadOnlyGrant = "cli-read";

    public static string RepoRoot { get; } = FindRepoRoot();

    public CliFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "vault-cli-tests-" + Guid.NewGuid().ToString("N"));
        VaultPath = Path.Combine(Root, "test");
        CopyDirectory(Path.Combine(RepoRoot, "test-vault"), VaultPath);

        OpClassesPath = Path.Combine(Root, "op-classes.json");
        File.Copy(Path.Combine(RepoRoot, "op-classes.json"), OpClassesPath);

        // The committed declaration, named the way the shipped vaults.json names it - resolved
        // against the directory holding vaults.json, which here is the fixture root.
        File.Copy(Path.Combine(RepoRoot, "frontmatter-registry.json"), Path.Combine(Root, "frontmatter-registry.json"));

        VaultsPath = Write("vaults.json", new
        {
            default_vault = "test",
            vaults = new
            {
                test = new
                {
                    path = VaultPath.Replace('\\', '/'),
                    frontmatter_registry = "frontmatter-registry.json",
                },
            },
        });

        GrantsPath = Write("grants.json", new Dictionary<string, object>
        {
            [AdminGrant] = new { named = true, id = AdminGrant, opClasses = new[] { "*" }, vaults = new[] { "*" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["*"] = ["*"] } },
            [ReadOnlyGrant] = new { named = true, id = ReadOnlyGrant, opClasses = new[] { "content:read" }, vaults = new[] { "test" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["test"] = ["*"] } },
        });

        SettingsPath = WriteSettings("vault.settings.json", AdminGrant, VaultsPath, GrantsPath, OpClassesPath);
    }

    public string Root { get; }

    public string VaultPath { get; }

    public string OpClassesPath { get; }

    public string VaultsPath { get; }

    public string GrantsPath { get; }

    /// <summary>The settings file every case uses unless it wants a broken one.</summary>
    public string SettingsPath { get; }

    /// <summary>Runs the shipped entry point against <paramref name="settingsPath"/>, defaulting to
    /// this fixture's own.</summary>
    public CliRun Run(string[] args, string? settingsPath = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = VaultCliApp.Run(args, output, error, settingsPath ?? SettingsPath);
        return new CliRun(exit, output.ToString(), error.ToString());
    }

    /// <summary>A settings file of this fixture's own shape, with one thing about it changed - the
    /// fail-closed matrix is a set of single deviations from a configuration that works.</summary>
    public string WriteSettings(string name, string? grantId, string vaultsPath, string grantsPath, string opClassesPath)
    {
        var settings = new Dictionary<string, string>
        {
            ["OpClassesConfigPath"] = opClassesPath,
            ["VaultsConfigPath"] = vaultsPath,
            ["GrantsConfigPath"] = grantsPath,
        };
        if (grantId is not null)
            settings["GrantId"] = grantId;

        return Write(name, settings);
    }

    /// <summary>Writes <paramref name="content"/> into the fixture root and answers its path.</summary>
    public string Write(string name, object content)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Writes verbatim text - what a malformed-configuration case needs, since a serializer
    /// cannot produce one.</summary>
    public string WriteText(string name, string content)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "test-vault")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;

        throw new InvalidOperationException("Repo root containing test-vault/ not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
