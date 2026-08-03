using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// Everything this invocation needs, constructed exactly as the server constructs it: the same load
/// cascade over the same three files, the same frontmatter registries, the same tool registry, and
/// the same shared execute path. What differs is what a failure means and where authority comes
/// from, which is the whole of what a front-end supplies.
///
/// <b>The cascade fails at the first fatal problem.</b> The loaders record rather than throw so that
/// a host can decide; this host has one command to run and nothing to stay up for, so it stops. That
/// also makes the server's cascade-avoidance skip unnecessary here - grants are never validated
/// against an empty op-class registry, because the invocation is already over by then.
/// </summary>
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

    public CliSettings Settings { get; }

    public CliHealthState Health { get; }

    public VaultConfig VaultConfig { get; }

    public GrantStore Grants { get; }

    public ToolRegistry Tools { get; }

    public ToolExecution Execution { get; }

    /// <summary>
    /// Runs the cascade and builds the tool registry.
    /// </summary>
    /// <param name="diagnostics">Where the execute path's inherited-lock signal goes - the standard
    /// error stream, so it never mixes into the answer on standard output.</param>
    /// <exception cref="CliFailure">Any of the three configuration files failed to load.</exception>
    public static CliHost Load(CliSettings settings, TextWriter diagnostics)
    {
        var health = new CliHealthState();

        var opClasses = OpClassRegistryLoader.Load(settings.OpClassesConfigPath, health);
        Fail(health, settings, settings.OpClassesConfigPath);

        var vaultConfig = VaultConfigLoader.Load(settings.VaultsConfigPath, opClasses, health);
        Fail(health, settings, settings.VaultsConfigPath);

        // No LocalhostAuthOptions is constructed, and that absence is the design rather than an
        // omission: the localhost resolution path is built never to error, because a floor is safer
        // than a rejection on a server that only listens to localhost. The CLI's equivalent
        // condition must error, so it resolves a named grant by id instead - see Grant below.
        var grants = GrantStore.Load(settings.GrantsConfigPath, opClasses, vaultConfig, health);
        Fail(health, settings, settings.GrantsConfigPath);

        // Resolved against the directory holding vaults.json, which is where each vault's
        // declaration is named from - the same seam the server loads them through.
        var frontmatter = FrontmatterRegistries.Load(
            vaultConfig, Path.GetDirectoryName(settings.VaultsConfigPath)!, opClasses, health);

        var engine = settings.RipgrepPath is { Length: > 0 } path ? new SearchEngine(path) : SearchEngine.OnPath;

        // The order is the order the command tree lists subcommands in. The two tools declaring
        // RequiresClientSession are not here at all: they mutate per-process authority state keyed to
        // a live caller's token, and they are the server's own besides. What keeps them off this
        // front-end is the execute path's session check, which a tree filter only reflects.
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
    /// The grant this invocation runs as: the id <c>--grant</c> named, else the one the settings
    /// file names. It authenticates nothing - it selects - and the operator's choice of a wider
    /// grant is what elevation is at this front-end.
    ///
    /// <b>Absence fails, and so does an id nothing defines.</b> No path through here turns a missing
    /// value into authority, and a token-bearing row is not selectable by id: only the token-less
    /// named grants are, which is what <see cref="GrantStore.TryGetNamedGrant"/> answers for.
    /// </summary>
    /// <param name="requested">The <c>--grant</c> value, or null.</param>
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

    /// <summary>Stops the invocation on the first load that went fatal, naming the file and
    /// everything that load recorded about it.</summary>
    private static void Fail(CliHealthState health, CliSettings settings, string path)
    {
        if (!health.IsFatallyDegraded)
            return;

        throw CliFailure.Configuration(
            $"Configuration \"{path}\" would not load, so this invocation cannot establish the grant it must run under: {health.FatalSummary} " +
            $"(configuration named by \"{settings.SettingsPath}\")");
    }
}
