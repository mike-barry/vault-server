using Microsoft.AspNetCore.Http;
using VaultMcpServer.Auth;
using VaultShared.Access;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultShared.Health;
using VaultMcpServer.Logging;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Tools;

var builder = WebApplication.CreateBuilder(args);

// The Kestrel listener bind, read from configuration with a code default. Plain HTTP only: the
// trust model here is the localhost auth floor (see BearerGrantMiddleware and GrantStore), not
// TLS. A comma-separated list is accepted, matching UseUrls' own semantics.
builder.WebHost.UseUrls(builder.Configuration["ListenUrls"] ?? "http://localhost:5111");

// The file sink for ILogger, registered with the standard logging builder so appsettings'
// "Logging" section governs its level filtering just as it does the console provider's. The
// provider needs no DI, so it can be constructed and written to before builder.Build(), but an
// ILogger<T> cannot be resolved until the service provider exists.
var logsDirectory = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["LogsDirectory"] ?? "../../logs"));
builder.Logging.AddProvider(new FileLoggerProvider(logsDirectory));

// Collects the config problems the loaders below record instead of throwing, so the process
// always starts. Constructed directly rather than through DI because the loaders need it before
// builder.Build() produces a service provider, and registered as a singleton so
// BearerGrantMiddleware resolves this same instance.
var healthState = new ServerHealthState();
builder.Services.AddSingleton(healthState);

var opClassesConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["OpClassesConfigPath"] ?? "../../op-classes.json"));
var opClassRegistry = OpClassRegistryLoader.Load(opClassesConfigPath, healthState);
builder.Services.AddSingleton(opClassRegistry);

var vaultsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["VaultsConfigPath"] ?? "../../vaults.json"));
var vaultConfig = VaultConfigLoader.Load(vaultsConfigPath, opClassRegistry, healthState);
builder.Services.AddSingleton(vaultConfig);

// GrantStore.Load validates each row's opClasses and vaults against the op-class registry and
// the vault config. If either of those already recorded a fatal problem, parsing grants.json
// would fail every row against an empty registry and bury the root cause under a list of
// "unknown opClass" and "unknown vault" errors, so an empty store is used instead.
var grantsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["GrantsConfigPath"] ?? "../../grants.json"));

// Localhost auth policy (see LocalhostAuthOptions and GrantStore.ResolveLocalhostGrant): the
// well-known non-secret selector token, an optional strict expected-token override, and the
// named grant the selector binds to on this machine. The grant id is optional; without it the
// selector resolves to the minimum-access floor, so naming one here is what widens it.
var localhostAuthOptions = new LocalhostAuthOptions(
    SelectorToken: builder.Configuration["LocalhostAuth:SelectorToken"] is { Length: > 0 } sel ? sel : LocalhostAuthOptions.DefaultSelectorToken,
    ExpectedLocalhostToken: builder.Configuration["LocalhostAuth:ExpectedLocalhostToken"] is { Length: > 0 } exp ? exp : null,
    DefaultGrantId: builder.Configuration["LocalhostAuth:DefaultGrantId"] is { Length: > 0 } def ? def : null);

GrantStore grantStore;
if (healthState.IsFatallyDegraded)
{
    healthState.RecordDegraded("GrantStore", $"Skipped loading {grantsConfigPath}: a prior fatal error in op-classes.json or vaults.json would make every grant fail validation.");
    grantStore = GrantStore.Empty();
}
else
{
    grantStore = GrantStore.Load(grantsConfigPath, opClassRegistry, vaultConfig, healthState, localhostOptions: localhostAuthOptions);
}
builder.Services.AddSingleton(grantStore);

// The frontmatter declarations, one per vault and one instance per file. Loaded after op-classes
// because the load-time check that every declared authority is a real op class runs against that
// registry, and skipped after a prior fatal problem for the same reason grants are. A failed load
// here is Degraded and refuses only the frontmatter writes.
var frontmatterRegistries = healthState.IsFatallyDegraded
    ? FrontmatterRegistries.None
    : FrontmatterRegistries.Load(vaultConfig, Path.GetDirectoryName(vaultsConfigPath)!, opClassRegistry, healthState);
builder.Services.AddSingleton(frontmatterRegistries);

var toolsDir = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["ToolsDirectory"] ?? "../../tools"));
var scriptToolRegistry = new ScriptToolRegistry(toolsDir, opClassRegistry, healthState);
builder.Services.AddSingleton(scriptToolRegistry);

// The search engine both the native tools and the tool scripts use, interpreted once from the
// RipgrepPath setting. A relative setting resolves against the content root, and a configured
// executable that is not there is recorded as a degraded problem and still used, so search and
// enumeration refuse dependency_unavailable while the six tools that never search keep serving.
var searchEngine = SearchEngines.Load(builder.Configuration["RipgrepPath"], builder.Environment.ContentRootPath, healthState);

// The runner is given the host's resolved vaults.json path so it can set VAULT_MCP_VAULTS_JSON
// before every script invocation. A script reads that variable to locate vaults.json, and
// re-asserting the path each call stops one tool script from redirecting a later one through the
// process-wide variable. The engine's location (passed as VAULT_MCP_RG_PATH) works the same way;
// when it is unset a script falls back to rg on PATH, which is the same fallback the native tools
// take.
var scriptToolRunner = new ScriptToolRunner(vaultsConfigPath, searchEngine.Executable);
builder.Services.AddSingleton(scriptToolRunner);

// Constructed and registered directly rather than through AddHttpContextAccessor(), so
// ScriptToolDispatcher can be wired up here, before builder.Build(). The framework still
// populates HttpContext on it per request once it is resolvable as IHttpContextAccessor.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

// Declared tools are compiled implementations, not loaded from disk like script tools. They are
// constructed here because ScriptToolDispatcher needs the registry before builder.Build()
// produces a service provider, and each is also registered as a singleton so it stays resolvable
// through DI by type.
var mintTokenTool = new MintTokenTool(grantStore, opClassRegistry, vaultConfig);
builder.Services.AddSingleton(mintTokenTool);
var healthCheckTool = new HealthCheckTool(healthState, vaultConfig);
builder.Services.AddSingleton(healthCheckTool);

// The elevation approver is chosen here in code, not by configuration, and auto-deny is the
// default so nothing can escalate unless an approver is deliberately wired in. It is constructed
// before builder.Build(), so its logger is attached after the build below.
var autoDenyApprover = new AutoDenyElevationApprover();
builder.Services.AddSingleton<IElevationApprover>(autoDenyApprover);
var elevationOptions = new ElevationOptions(
    DefaultTtlMinutes: builder.Configuration.GetValue("Elevation:DefaultTtlMinutes", 15),
    MaxRequestsPerMinute: builder.Configuration.GetValue("Elevation:MaxRequestsPerMinute", 5));
var elevationRequestTool = new ElevationRequestTool(
    grantStore, autoDenyApprover, opClassRegistry, vaultConfig, elevationOptions, TimeProvider.System);
builder.Services.AddSingleton(elevationRequestTool);

// The registry is keyed by declaration, so adding a tool means a `new` plus a line here. This
// order is the order tools/list reports them in.
//
// A vault tool's vault, grant, and session all arrive on the call, so one instance can answer
// every caller. The only per-caller-independent state any of them holds is the per-vault
// frontmatter declarations, which only the two noted below need. The three tools above hold
// server state instead, which is why they are constructed with it.
var toolRegistry = new ToolRegistry()
    .Add(MintTokenTool.Declaration, mintTokenTool)
    .Add(HealthCheckTool.Declaration, healthCheckTool)
    .Add(ElevationRequestTool.Declaration, elevationRequestTool)
    .Add(ReadTool.Declaration, new ReadTool())
    .Add(GlobTool.Declaration, new GlobTool())
    .Add(GrepTool.Declaration, new GrepTool())
    // The two tools that hold the frontmatter declarations: those are per vault and loaded once
    // at startup, while a call carries only a vault name. Both write a note's frontmatter through
    // the same write path - edit applies operations, create validates a whole supplied block.
    .Add(EditTool.Declaration, new EditTool(frontmatterRegistries))
    .Add(CreateTool.Declaration, new CreateTool(frontmatterRegistries))
    .Add(MoveTool.Declaration, new MoveTool())
    .Add(DeleteTool.Declaration, new DeleteTool());
builder.Services.AddSingleton(toolRegistry);

// The shared execute path. The inherited-lock signal is a delegate rather than an ILogger because
// VaultShared takes no dependency on a logging abstraction; it is bound to the host's logger once
// one exists.
ILogger? executionLogger = null;
var toolExecution = new ToolExecution(
    vaultConfig,
    searchEngine,
    onInheritedLock: message => executionLogger?.LogWarning("{Message}", message));
builder.Services.AddSingleton(toolExecution);

var scriptToolDispatcher = new ScriptToolDispatcher(scriptToolRegistry, toolRegistry, toolExecution, scriptToolRunner, httpContextAccessor, healthState, vaultConfig);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
        // In stateless mode this runs on every HTTP request, after BearerGrantMiddleware, with
        // a fresh McpServerOptions each time. It must stay cheap, so it copies healthState's
        // cached InstructionsSummary rather than assembling anything from Problems. An empty
        // summary leaves ServerInstructions null, so a healthy server's initialize response
        // says nothing extra.
        options.ConfigureSessionOptions = (_, serverOptions, _) =>
        {
            var summary = healthState.InstructionsSummary;
            serverOptions.ServerInstructions = summary.Length > 0 ? summary : null;
            return Task.CompletedTask;
        };
    })
    .WithListToolsHandler(scriptToolDispatcher.ListToolsAsync)
    .WithCallToolHandler(scriptToolDispatcher.CallToolAsync);

var app = builder.Build();

app.UseMiddleware<BearerGrantMiddleware>();
app.MapMcp("/mcp");

scriptToolDispatcher.AttachLogger(app.Services.GetRequiredService<ILogger<ScriptToolDispatcher>>());
executionLogger = app.Services.GetRequiredService<ILogger<ToolExecution>>();
autoDenyApprover.AttachLogger(app.Services.GetRequiredService<ILogger<AutoDenyElevationApprover>>());
var scriptToolsWatcher = new ScriptToolsWatcher(
    toolsDir, scriptToolRegistry, scriptToolDispatcher, app.Services.GetRequiredService<ILogger<ScriptToolsWatcher>>());

// Logs every problem ServerHealthState collected during the config loads above, which all ran
// before builder.Build() and so had no ILogger to write to. Fatal problems log as errors and
// degraded ones as warnings. Nothing here decides IsFatallyDegraded; it only reports what the
// loaders recorded.
foreach (var problem in healthState.Problems)
{
    var level = problem.Severity == ProblemSeverity.Fatal ? LogLevel.Error : LogLevel.Warning;
    app.Logger.Log(level, "Config problem [{Severity}] {Source}: {Detail}", problem.Severity, problem.Source, problem.Detail);
}
if (healthState.IsFatallyDegraded)
    app.Logger.LogError("Server is fatally degraded - every request will be rejected with 503 until this is fixed and the server is restarted.");

// From here on, problems recorded at runtime by the tools directory watcher's re-scans log
// themselves as they are recorded. The logger is attached after the loop above so that nothing
// recorded during startup is logged twice.
healthState.AttachLogger(app.Logger);

app.Logger.LogInformation("Op classes loaded from {Path}: {OpClasses}", opClassesConfigPath, string.Join(", ", opClassRegistry.Names));
app.Logger.LogInformation(
    "Vaults loaded from {Path}: {Vaults} (default: {Default})",
    vaultsConfigPath, string.Join(", ", vaultConfig.Vaults.Keys), vaultConfig.DefaultVault);
foreach (var (vaultName, vaultEntry) in vaultConfig.Vaults.Where(v => v.Value.PathPolicy.AlwaysReadableRoots.Count > 0))
    app.Logger.LogInformation(
        "Vault {Vault} always-readable roots: {Roots}",
        vaultName, string.Join(", ", vaultEntry.PathPolicy.AlwaysReadableRoots));
app.Logger.LogInformation("Grants loaded from {Path}", grantsConfigPath);
app.Logger.LogInformation("Tools loaded from {Path}: {Count}", toolsDir, scriptToolRegistry.Tools.Count);

app.Lifetime.ApplicationStopping.Register(scriptToolRunner.Dispose);
app.Lifetime.ApplicationStopping.Register(scriptToolsWatcher.Dispose);

app.Run();

public partial class Program;
