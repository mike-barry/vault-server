using Microsoft.AspNetCore.Http;
using VaultMcpServer.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Logging;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

var builder = WebApplication.CreateBuilder(args);

// The Kestrel listener bind, an appsetting like the path settings below - resolved straight off
// IConfiguration with a code default so an unset value preserves the historical
// http://localhost:5111 behavior. Plain HTTP only; the localhost auth floor (see
// BearerGrantMiddleware / GrantStore) is the trust model here, not TLS. A comma-separated list is
// honored, matching UseUrls' own semantics.
builder.WebHost.UseUrls(builder.Configuration["ListenUrls"] ?? "http://localhost:5111");

// File sink for ILogger, wired into the standard logging builder now so appsettings'
// "Logging" section governs its level filtering exactly like the console provider - no
// bespoke level machinery. The provider itself can be constructed and written to before
// builder.Build() (it needs no DI), but an actual ILogger<T> can't be resolved until the
// service provider exists - see the health-problem buffering note below for how the config
// loads' Fatal/Degraded problems, recorded before that point, still reach this file.
var logsDirectory = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["LogsDirectory"] ?? "../../logs"));
builder.Logging.AddProvider(new FileLoggerProvider(logsDirectory));

// Records config problems instead of the loaders below throwing, so the process always
// starts - see ServerHealthState's remarks. Constructed directly (not via DI) for the same
// reason scriptToolRegistry/scriptToolRunner are below: the loaders need it synchronously,
// before builder.Build() produces a service provider. Registered as a singleton so
// BearerGrantMiddleware can resolve the same instance later to short-circuit with 503.
var healthState = new ServerHealthState();
builder.Services.AddSingleton(healthState);

var opClassesConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["OpClassesConfigPath"] ?? "../../op-classes.json"));
var opClassRegistry = OpClassRegistry.Load(opClassesConfigPath, healthState);
builder.Services.AddSingleton(opClassRegistry);

var vaultsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["VaultsConfigPath"] ?? "../../vaults.json"));
var vaultConfig = VaultConfig.Load(vaultsConfigPath, healthState);
builder.Services.AddSingleton(vaultConfig);

// GrantStore.Load validates each row's opClasses/vaults against opClassRegistry/vaultConfig,
// so if either already went fatal above, a real parse of grants.json would fail every row
// against an empty registry and pile a second, confusing "unknown opClass"/"unknown vault"
// fatal on top of the real root cause. Skip straight to an empty store in that case instead -
// see GrantStore.Empty's remarks.
var grantsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["GrantsConfigPath"] ?? "../../grants.json"));

// Localhost auth policy (see LocalhostAuthOptions / GrantStore.ResolveLocalhostGrant): the
// well-known non-secret selector token, an optional strict expected-token override, and the
// named grant the selector binds to on this machine (the dev default - absent means the selector
// floors to minimum access, i.e. the switchover to a live dev default is a later batch).
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

var toolsDir = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["ToolsDirectory"] ?? "../../tools"));
var scriptToolRegistry = new ScriptToolRegistry(toolsDir, opClassRegistry, healthState);
builder.Services.AddSingleton(scriptToolRegistry);

// Pass the host's resolved vaults.json path so the runner can pin VAULT_MCP_VAULTS_JSON to it
// ahead of every script invocation - a script reads that env var to locate vaults.json, and
// re-asserting the intended path each call stops a prior tool script from redirecting a later one
// via the process-wide variable. The ripgrep path (RipgrepPath appsetting -> VAULT_MCP_RG_PATH)
// rides the same mechanism; unset means a script falls back to rg on PATH.
var ripgrepPath = builder.Configuration["RipgrepPath"] is { Length: > 0 } rgp
    ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, rgp))
    : null;
var scriptToolRunner = new ScriptToolRunner(vaultsConfigPath, ripgrepPath);
builder.Services.AddSingleton(scriptToolRunner);

// A directly-constructed, directly-registered instance (rather than AddHttpContextAccessor())
// so ScriptToolDispatcher and the elevation tool can be wired up here, before builder.Build() -
// the framework still populates HttpContext on it per-request once it's resolvable as
// IHttpContextAccessor.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

// Native tools are compiled implementations (not hot-reloaded from disk like script tools).
// Constructed directly here - like scriptToolRegistry/scriptToolRunner above - because
// ScriptToolDispatcher needs the registry synchronously, before builder.Build() produces a
// service provider. Each is also registered as its own singleton instance below so it is
// resolvable through DI by type, same as GrantStore/VaultConfig/ScriptToolRegistry - a second
// INativeTool only needs a `new` here plus an entry in this list, no dispatcher change.
var mintTokenTool = new MintTokenTool(grantStore, opClassRegistry, vaultConfig);
builder.Services.AddSingleton<INativeTool>(mintTokenTool);
var healthCheckTool = new HealthCheckTool(healthState, vaultConfig);
builder.Services.AddSingleton<INativeTool>(healthCheckTool);

// Elevation: the approval seam is the IElevationApprover interface, and the implementation is
// chosen here in code, not by config. Auto-deny is the hardcoded default - the honest one, so
// nothing can escalate by default. Constructed before builder.Build(); logger attached post-build.
var autoDenyApprover = new AutoDenyElevationApprover();
builder.Services.AddSingleton<IElevationApprover>(autoDenyApprover);
var elevationOptions = new ElevationOptions(
    DefaultTtlMinutes: builder.Configuration.GetValue("Elevation:DefaultTtlMinutes", 15),
    MaxRequestsPerMinute: builder.Configuration.GetValue("Elevation:MaxRequestsPerMinute", 5));
var elevationRequestTool = new ElevationRequestTool(
    grantStore, autoDenyApprover, httpContextAccessor, opClassRegistry, vaultConfig, elevationOptions, TimeProvider.System);
builder.Services.AddSingleton<INativeTool>(elevationRequestTool);

var nativeToolRegistry = new NativeToolRegistry([mintTokenTool, healthCheckTool, elevationRequestTool]);
builder.Services.AddSingleton(nativeToolRegistry);

var scriptToolDispatcher = new ScriptToolDispatcher(scriptToolRegistry, nativeToolRegistry, scriptToolRunner, httpContextAccessor, healthState, vaultConfig);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
        // Runs on every HTTP request in stateless mode (a fresh McpServerOptions per
        // request, so no cross-request race), after BearerGrantMiddleware. Must stay cheap:
        // it copies healthState's pre-rendered, cached InstructionsSummary rather than
        // assembling anything from Problems itself - see that property's remarks. Empty
        // summary (no problems) leaves ServerInstructions null, so an initialize response
        // says nothing extra while the server is healthy.
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
autoDenyApprover.AttachLogger(app.Services.GetRequiredService<ILogger<AutoDenyElevationApprover>>());
var scriptToolsWatcher = new ScriptToolsWatcher(
    toolsDir, scriptToolRegistry, scriptToolDispatcher, app.Services.GetRequiredService<ILogger<ScriptToolsWatcher>>());

// Flushes every problem ServerHealthState buffered during the config loads above - those
// all ran before builder.Build(), so there was no DI ILogger yet to log them to directly.
// Fatal -> Error, Degraded -> Warning; nothing here decides IsFatallyDegraded, it only
// reports what the loaders already recorded.
foreach (var problem in healthState.Problems)
{
    var level = problem.Severity == ProblemSeverity.Fatal ? LogLevel.Error : LogLevel.Warning;
    app.Logger.Log(level, "Config problem [{Severity}] {Source}: {Detail}", problem.Severity, problem.Source, problem.Detail);
}
if (healthState.IsFatallyDegraded)
    app.Logger.LogError("Server is fatally degraded - every request will be rejected with 503 until this is fixed and the server is restarted.");

// From here on, problems recorded at runtime (the tools/ watcher's re-scans replacing the
// ScriptTools problem set) log themselves at record time - attached only AFTER the flush
// above so nothing recorded during startup is logged twice.
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
