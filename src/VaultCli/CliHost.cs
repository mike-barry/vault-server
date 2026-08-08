using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// Holds everything one invocation needs, built the same way the server builds it: the same load
/// cascade over the same three configuration files, the same frontmatter registries, the same tool
/// registry and the same shared execute path. Only what a failure means and where authority comes
/// from differ, and those are what a front-end supplies.
/// </summary>
/// <remarks>
/// The cascade stops at the first fatal problem. The loaders record rather than throw so the host
/// can decide, and this host has one command to run, so it fails the invocation. That is also why
/// the server's skip for an already-failed cascade is not needed here: grants are never validated
/// against an empty op-class registry, because the invocation has already ended.
/// </remarks>
public sealed class CliHost
{
    private CliHost(
        CliSettings settings,
        CliHealthState health,
        VaultConfig vaultConfig,
        GrantStore grants,
        ToolRegistry tools,
        ToolExecution execution)
    {
        Settings = settings;
        Health = health;
        VaultConfig = vaultConfig;
        Grants = grants;
        Tools = tools;
        Execution = execution;
    }

    /// <summary>Gets the settings this host was built from.</summary>
    public CliSettings Settings { get; }

    /// <summary>Gets the problems the load cascade recorded.</summary>
    public CliHealthState Health { get; }

    /// <summary>Gets the loaded vault configuration.</summary>
    public VaultConfig VaultConfig { get; }

    /// <summary>Gets the loaded grants.</summary>
    public GrantStore Grants { get; }

    /// <summary>Gets the tools this front-end exposes.</summary>
    public ToolRegistry Tools { get; }

    /// <summary>Gets the shared execute path every tool call goes through.</summary>
    public ToolExecution Execution { get; }

    /// <summary>
    /// Runs the load cascade and builds the tool registry.
    /// </summary>
    /// <param name="settings">The CLI settings naming the configuration files to load.</param>
    /// <param name="diagnostics">Where the execute path's inherited-lock message is written. Pass
    /// the standard error stream so it never mixes into the answer on standard output.</param>
    /// <returns>The host for this invocation.</returns>
    /// <exception cref="CliFailure">Any of the three configuration files failed to load.</exception>
    public static CliHost Load(CliSettings settings, TextWriter diagnostics)
    {
        var health = new CliHealthState();

        var opClasses = OpClassRegistryLoader.Load(settings.OpClassesConfigPath, health);
        Fail(health, settings, settings.OpClassesConfigPath);

        var vaultConfig = VaultConfigLoader.Load(settings.VaultsConfigPath, opClasses, health);
        Fail(health, settings, settings.VaultsConfigPath);

        // No LocalhostAuthOptions is constructed here on purpose. The server's localhost resolution
        // never errors, because falling back to a floor grant is safer than rejecting a caller on a
        // server that only listens to localhost. The CLI must error instead, so it resolves a named
        // grant by id - see Grant below.
        var grants = GrantStore.Load(settings.GrantsConfigPath, opClasses, vaultConfig, health);
        Fail(health, settings, settings.GrantsConfigPath);

        // Frontmatter declaration paths resolve against the directory holding vaults.json, because
        // that is where each vault names its declaration from. The server loads them the same way.
        var frontmatter = FrontmatterRegistries.Load(
            vaultConfig, Path.GetDirectoryName(settings.VaultsConfigPath)!, opClasses, health);

        // The engine's configured location resolves against the directory holding the settings file,
        // which is the file that names it. A location that is not there is recorded as degraded and
        // still used, so search refuses and every tool that does not search keeps working.
        var engine = SearchEngines.Load(
            settings.RipgrepPath, Path.GetDirectoryName(settings.SettingsPath)!, health);

        // This order is the order the command tree lists its subcommands in. The two tools that
        // declare RequiresClientSession are not registered here: they change per-process authority
        // state keyed to a live caller's token, which only the server has. Their real enforcement is
        // the execute path's session check; leaving them out of the registry only reflects it.
        var tools = new ToolRegistry()
            .Add(HealthCheckTool.Declaration, new HealthCheckTool(health, vaultConfig))
            .Add(ReadTool.Declaration, new ReadTool())
            .Add(GlobTool.Declaration, new GlobTool())
            .Add(GrepTool.Declaration, new GrepTool())
            .Add(EditTool.Declaration, new EditTool(frontmatter))
            .Add(CreateTool.Declaration, new CreateTool(frontmatter))
            .Add(MoveTool.Declaration, new MoveTool())
            .Add(DeleteTool.Declaration, new DeleteTool());

        var execution = new ToolExecution(
            vaultConfig,
            engine,
            onInheritedLock: message => diagnostics.WriteLine(message));

        return new CliHost(settings, health, vaultConfig, grants, tools, execution);
    }

    /// <summary>
    /// Resolves the grant this invocation runs as: the id <c>--grant</c> gave, otherwise the id the
    /// settings file names. This selects a grant rather than authenticating one, so choosing a wider
    /// grant is how an operator elevates at this front-end.
    /// </summary>
    /// <remarks>
    /// Both a missing id and an id nothing defines fail the invocation, so no path here turns a
    /// missing value into authority. Only token-less named grants are selectable by id, which is
    /// what <see cref="GrantStore.TryGetNamedGrant"/> checks; a token-bearing row cannot be chosen
    /// this way.
    /// </remarks>
    /// <param name="requested">The <c>--grant</c> value, or null when it was not supplied.</param>
    /// <returns>The grant every tool call in this invocation runs under.</returns>
    /// <exception cref="CliFailure">No id was named, or the named id is not a named grant.</exception>
    public Grant Grant(string? requested)
    {
        var id = requested is { Length: > 0 } named ? named : Settings.GrantId;
        if (id is not { Length: > 0 })
        {
            throw CliFailure.Configuration(
                $"No grant is named. Set \"GrantId\" in \"{Settings.SettingsPath}\" to a named grant in \"{Settings.GrantsConfigPath}\", or pass --grant. " +
                "A missing setting is not an unrestricted default.");
        }

        if (!Grants.TryGetNamedGrant(id, out var grant))
        {
            var source = requested is { Length: > 0 } ? "--grant" : $"\"GrantId\" in \"{Settings.SettingsPath}\"";
            throw CliFailure.Configuration(
                $"{source} names \"{id}\", which is not a named grant in \"{Settings.GrantsConfigPath}\". " +
                "A named grant is a token-less row carrying \"named\": true, and only those are selectable by id.",
                new { grant = id });
        }

        return grant;
    }

    /// <summary>Stops the invocation if the load so far recorded a fatal problem, naming the file
    /// and everything recorded about it.</summary>
    /// <param name="health">The problems recorded by the cascade so far.</param>
    /// <param name="settings">The settings that named the configuration files.</param>
    /// <param name="path">The configuration file that was just loaded.</param>
    /// <exception cref="CliFailure">A fatal problem was recorded.</exception>
    private static void Fail(CliHealthState health, CliSettings settings, string path)
    {
        if (!health.IsFatallyDegraded)
            return;

        throw CliFailure.Configuration(
            $"Configuration \"{path}\" would not load, so this invocation cannot establish the grant it must run under: {health.FatalSummary} " +
            $"(configuration named by \"{settings.SettingsPath}\")");
    }
}
