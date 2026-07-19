using Microsoft.AspNetCore.Http;
using VaultMcpServer.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Logging;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5111");

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
builder.Services.AddSingleton<VaultAccess>();

// GrantStore.Load validates each row's opClasses/vaults against opClassRegistry/vaultConfig,
// so if either already went fatal above, a real parse of grants.json would fail every row
// against an empty registry and pile a second, confusing "unknown opClass"/"unknown vault"
// fatal on top of the real root cause. Skip straight to an empty store in that case instead -
// see GrantStore.Empty's remarks.
var grantsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["GrantsConfigPath"] ?? "../../grants.json"));
GrantStore grantStore;
if (healthState.IsFatallyDegraded)
{
    healthState.RecordDegraded("GrantStore", $"Skipped loading {grantsConfigPath}: a prior fatal error in op-classes.json or vaults.json would make every grant fail validation.");
    grantStore = GrantStore.Empty();
}
else
{
    grantStore = GrantStore.Load(grantsConfigPath, opClassRegistry, vaultConfig, healthState);
}
builder.Services.AddSingleton(grantStore);

var toolsDir = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["ToolsDirectory"] ?? "../../tools"));
var scriptToolRegistry = new ScriptToolRegistry(toolsDir, opClassRegistry);
builder.Services.AddSingleton(scriptToolRegistry);

var scriptToolRunner = new ScriptToolRunner();
builder.Services.AddSingleton(scriptToolRunner);

// Native tools are compiled implementations (not hot-reloaded from disk like script tools).
// Constructed directly here - like scriptToolRegistry/scriptToolRunner above - because
// ScriptToolDispatcher needs the registry synchronously, before builder.Build() produces a
// service provider. Each is also registered as its own singleton instance below so it is
// resolvable through DI by type, same as GrantStore/VaultConfig/ScriptToolRegistry - a second
// INativeTool only needs a `new` here plus an entry in this list, no dispatcher change.
var mintTokenTool = new MintTokenTool(grantStore, opClassRegistry, vaultConfig);
builder.Services.AddSingleton<INativeTool>(mintTokenTool);
var nativeToolRegistry = new NativeToolRegistry([mintTokenTool]);
builder.Services.AddSingleton(nativeToolRegistry);

// A directly-constructed, directly-registered instance (rather than AddHttpContextAccessor())
// so ScriptToolDispatcher can be wired up here, before builder.Build() - the framework
// still populates HttpContext on it per-request once it's resolvable as IHttpContextAccessor.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

var scriptToolDispatcher = new ScriptToolDispatcher(scriptToolRegistry, nativeToolRegistry, scriptToolRunner, httpContextAccessor);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithListToolsHandler(scriptToolDispatcher.ListToolsAsync)
    .WithCallToolHandler(scriptToolDispatcher.CallToolAsync);

var app = builder.Build();

app.UseMiddleware<BearerGrantMiddleware>();
app.MapMcp("/mcp");

scriptToolDispatcher.AttachLogger(app.Services.GetRequiredService<ILogger<ScriptToolDispatcher>>());
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

app.Logger.LogInformation("Op classes loaded from {Path}: {OpClasses}", opClassesConfigPath, string.Join(", ", opClassRegistry.Names));
app.Logger.LogInformation(
    "Vaults loaded from {Path}: {Vaults} (default: {Default})",
    vaultsConfigPath, string.Join(", ", vaultConfig.Vaults.Keys), vaultConfig.DefaultVault);
app.Logger.LogInformation("Grants loaded from {Path}", grantsConfigPath);
app.Logger.LogInformation("Tools loaded from {Path}: {Count}", toolsDir, scriptToolRegistry.Tools.Count);

app.Lifetime.ApplicationStopping.Register(scriptToolRunner.Dispose);
app.Lifetime.ApplicationStopping.Register(scriptToolsWatcher.Dispose);

app.Run();

public partial class Program;
