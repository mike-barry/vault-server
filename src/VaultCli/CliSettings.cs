using System.Text.Json;

namespace VaultCli;

/// <summary>
/// The CLI's own configuration: a settings file beside the executable, following the retained
/// configuration idioms - the same key names the server's <c>appsettings.json</c> uses for the same
/// three files, plus the grant id that is this front-end's alone.
///
/// <b>It is not <c>vaults.json</c>.</b> The grant setting has to live in a file that cannot be read
/// as governing the server too, and the three config paths have to be nameable by a CLI deployed
/// somewhere other than beside the server.
///
/// <b>Relative paths resolve against the directory holding this file</b>, never the process's
/// working directory - a CLI is run from wherever the operator happens to be standing, and a
/// configuration that moved with them would be a different configuration per invocation.
/// </summary>
/// <param name="SettingsPath">The file this was read from, reported in a failure so an operator
/// knows which file to fix.</param>
/// <param name="GrantId">The named grant every invocation runs as, unless <c>--grant</c> overrides
/// it. No default: absence fails the invocation rather than admitting it unrestricted.</param>
public sealed record CliSettings(
    string SettingsPath,
    string? GrantId,
    string OpClassesConfigPath,
    string VaultsConfigPath,
    string GrantsConfigPath,
    string? RipgrepPath)
{
    /// <summary>The file name looked for beside the executable.</summary>
    public const string FileName = "vault.settings.json";

    /// <summary>
    /// Comments and trailing commas are read, so the shipped file can explain what each setting is
    /// for. Nothing else about the parse is lenient: an unreadable or malformed file fails the
    /// invocation.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The settings file beside the running executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>. Every failure here is fatal: this file names where the
    /// authority configuration lives, so a process that cannot read it has no way to establish the
    /// grant every invocation carries.
    /// </summary>
    /// <exception cref="CliFailure">The file is absent, unreadable, or not a JSON object of
    /// settings.</exception>
    public static CliSettings Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw CliFailure.Configuration($"The CLI settings file \"{full}\" does not exist. It names the grant this front-end runs as and where the vault, grant and op-class configuration live.");

        Dictionary<string, JsonElement>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(full), ReadOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw CliFailure.Configuration($"The CLI settings file \"{full}\" could not be read: {exception.Message}");
        }

        if (values is null)
            throw CliFailure.Configuration($"The CLI settings file \"{full}\" is empty - it must be a JSON object of settings.");

        var directory = Path.GetDirectoryName(full)!;

        return new CliSettings(
            full,
            GrantId: Text(values, "GrantId"),
            // The three defaults are the deployment shape: vault.exe and the configuration it reads
            // sitting in one folder. The shipped file overrides them for the development tree, where
            // they sit at the repository root instead.
            OpClassesConfigPath: Resolve(directory, Text(values, "OpClassesConfigPath") ?? "op-classes.json"),
            VaultsConfigPath: Resolve(directory, Text(values, "VaultsConfigPath") ?? "vaults.json"),
            GrantsConfigPath: Resolve(directory, Text(values, "GrantsConfigPath") ?? "grants.json"),
            RipgrepPath: Text(values, "RipgrepPath") is { Length: > 0 } engine ? Resolve(directory, engine) : null);

        static string? Text(Dictionary<string, JsonElement> values, string key) =>
            values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : null;

        static string Resolve(string directory, string value) => Path.GetFullPath(Path.Combine(directory, value));
    }
}
