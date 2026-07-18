using Microsoft.AspNetCore.Http;
using VaultMcpServer.Auth;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5111");

var opClassesConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["OpClassesConfigPath"] ?? "../../op-classes.json"));
var opClassRegistry = OpClassRegistry.Load(opClassesConfigPath);
builder.Services.AddSingleton(opClassRegistry);

var vaultsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["VaultsConfigPath"] ?? "../../vaults.json"));
var vaultConfig = VaultConfig.Load(vaultsConfigPath);
builder.Services.AddSingleton(vaultConfig);
builder.Services.AddSingleton<VaultAccess>();

var grantsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["GrantsConfigPath"] ?? "../../grants.json"));
var grantStore = GrantStore.Load(grantsConfigPath, opClassRegistry, vaultConfig);
builder.Services.AddSingleton(grantStore);

var toolsDir = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["ToolsDirectory"] ?? "../../tools"));
var scriptToolRegistry = new ScriptToolRegistry(toolsDir, opClassRegistry);
builder.Services.AddSingleton(scriptToolRegistry);

var scriptToolRunner = new ScriptToolRunner();
builder.Services.AddSingleton(scriptToolRunner);

// A directly-constructed, directly-registered instance (rather than AddHttpContextAccessor())
// so ScriptToolDispatcher can be wired up here, before builder.Build() - the framework
// still populates HttpContext on it per-request once it's resolvable as IHttpContextAccessor.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

var scriptToolDispatcher = new ScriptToolDispatcher(scriptToolRegistry, scriptToolRunner, httpContextAccessor);

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
