using System.Text.Json;

namespace VaultCli;

/// <summary>
/// Represents the CLI's own configuration, read from a settings file beside the executable. It uses
/// the same key names the server's <c>appsettings.json</c> uses for the same three configuration
/// files, plus a grant id that belongs to this front-end alone.
/// </summary>
/// <remarks>
/// This is a separate file from <c>vaults.json</c> so that the grant setting cannot be read as
/// governing the server as well.
///
/// Relative paths resolve against the directory holding the settings file, never the process's
/// working directory. A CLI is run from wherever the operator happens to be, so resolving against
/// the working directory would give a different configuration per invocation.
/// </remarks>
/// <param name="SettingsPath">The file these settings were read from, reported in a failure so an
/// operator knows which file to fix.</param>
/// <param name="GrantId">The named grant every invocation runs as, unless <c>--grant</c> overrides
/// it. There is no default. Absence fails the invocation rather than admitting it
/// unrestricted.</param>
/// <param name="OpClassesConfigPath">The full path to the op-class configuration file.</param>
/// <param name="VaultsConfigPath">The full path to the vault configuration file.</param>
/// <param name="GrantsConfigPath">The full path to the grant configuration file.</param>
/// <param name="RipgrepPath">The ripgrep executable's location exactly as the settings file holds
/// it, which may be relative to the directory holding that file, or null when the setting is absent
/// or empty and the executable is found on <c>PATH</c>. It is carried unresolved because the shared
/// layer that interprets it applies the resolution base and the existence check for both
/// front-ends.</param>
public sealed record CliSettings(
    string SettingsPath,
    string? GrantId,
    string OpClassesConfigPath,
    string VaultsConfigPath,
    string GrantsConfigPath,
    string? RipgrepPath)
{
    /// <summary>The settings file name looked for beside the executable.</summary>
    public const string FileName = "vault.settings.json";

    /// <summary>
    /// The JSON reader options. Comments and trailing commas are allowed so the shipped file can
    /// explain what each setting is for.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Gets the path to the settings file beside the running executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Reads the settings from <paramref name="path"/>. Every failure here is fatal. This file names
    /// where the authority configuration lives, so a process that cannot read it has no way to
    /// establish the grant every invocation carries.
    /// </summary>
    /// <param name="path">The settings file to read. It may be relative to the working
    /// directory.</param>
    /// <returns>The settings, with the three configuration file paths resolved to full paths.</returns>
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
            // The three defaults assume the deployed shape: the executable and the configuration it
            // reads in one folder. The shipped settings file overrides them for the development
            // tree, where those files sit at the repository root instead.
            OpClassesConfigPath: Resolve(directory, Text(values, "OpClassesConfigPath") ?? "op-classes.json"),
            VaultsConfigPath: Resolve(directory, Text(values, "VaultsConfigPath") ?? "vaults.json"),
            GrantsConfigPath: Resolve(directory, Text(values, "GrantsConfigPath") ?? "grants.json"),
            RipgrepPath: Text(values, "RipgrepPath"));

        static string? Text(Dictionary<string, JsonElement> values, string key) =>
            values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : null;

        static string Resolve(string directory, string value) => Path.GetFullPath(Path.Combine(directory, value));
    }
}
