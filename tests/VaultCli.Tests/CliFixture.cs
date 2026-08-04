using System.Text.Json;
using VaultCli;

namespace VaultCli.Tests;

/// <summary>Represents the result of one CLI invocation: its exit code and each output stream
/// separately.</summary>
/// <param name="Exit">The process exit code the invocation returned.</param>
/// <param name="Output">Standard output, which carries the answer and nothing else.</param>
/// <param name="Error">Standard error, which carries a refusal envelope, a parser message, or a
/// diagnostic.</param>
public sealed record CliRun(int Exit, string Output, string Error)
{
    /// <summary>Parses the refusal envelope from standard error. A run that refused writes exactly
    /// one.</summary>
    public JsonElement Envelope => JsonDocument.Parse(Error.Trim()).RootElement;

    /// <summary>Returns the <c>code</c> field of the refusal envelope, which is the value a caller
    /// branches on regardless of which layer refused.</summary>
    public string Code => Envelope.GetProperty("code").GetString()!;
}

/// <summary>
/// Builds a complete CLI deployment in a temporary directory: a copy of the committed vault
/// fixture, the three configuration files, and a settings file naming them. Tests run the shipped
/// entry point against it, so what is exercised is a real invocation rather than a rearrangement of
/// its parts.
/// </summary>
/// <remarks>
/// Nothing here writes to the committed fixture. The vault is a copy, and cases that reach a
/// mutating tool take its preview path, which runs the same validation and the same lock without
/// writing.
///
/// Two named grants are defined. <c>cli-admin</c> is unrestricted and stands in for what the
/// shipped configuration supplies; <c>cli-read</c> holds <c>content:read</c> alone and is the
/// narrow grant refusals are asserted under. Both are token-less, because a grant id can only
/// select a token-less row.
/// </remarks>
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

        // A registry path in vaults.json resolves against the directory holding vaults.json, which
        // here is the fixture root, so the committed registry is copied there under its own name.
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

    /// <summary>Gets the path of the working settings file every case uses unless it supplies a
    /// deliberately broken one.</summary>
    public string SettingsPath { get; }

    /// <summary>Runs the shipped entry point and captures both output streams and the exit
    /// code.</summary>
    /// <param name="args">The command-line arguments to pass to the entry point.</param>
    /// <param name="settingsPath">The settings file to run against, or <see langword="null"/> to
    /// use this fixture's own.</param>
    /// <returns>The exit code and the text written to each stream.</returns>
    public CliRun Run(string[] args, string? settingsPath = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = VaultCliApp.Run(args, output, error, settingsPath ?? SettingsPath);
        return new CliRun(exit, output.ToString(), error.ToString());
    }

    /// <summary>Writes a settings file of this fixture's shape, letting a caller vary one part of
    /// it. The fail-closed cases each change a single value away from a configuration that
    /// works.</summary>
    /// <param name="name">The file name to write within the fixture root.</param>
    /// <param name="grantId">The grant id to record, or <see langword="null"/> to omit the setting
    /// entirely.</param>
    /// <param name="vaultsPath">The path to record for the vaults configuration.</param>
    /// <param name="grantsPath">The path to record for the grants configuration.</param>
    /// <param name="opClassesPath">The path to record for the operation-class configuration.</param>
    /// <returns>The full path of the settings file written.</returns>
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

    /// <summary>Serializes <paramref name="content"/> as JSON into the fixture root.</summary>
    /// <param name="name">The file name to write within the fixture root.</param>
    /// <param name="content">The object to serialize.</param>
    /// <returns>The full path of the file written.</returns>
    public string Write(string name, object content)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Writes text verbatim into the fixture root. Malformed-configuration cases need
    /// this, because a serializer cannot produce invalid JSON.</summary>
    /// <param name="name">The file name to write within the fixture root.</param>
    /// <param name="content">The exact text to write.</param>
    /// <returns>The full path of the file written.</returns>
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
