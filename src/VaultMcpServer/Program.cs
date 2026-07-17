using VaultMcpServer.Vault;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5111");

var vaultsConfigPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["VaultsConfigPath"] ?? "../../vaults.json"));
var vaultConfig = VaultConfig.Load(vaultsConfigPath);
builder.Services.AddSingleton(vaultConfig);
builder.Services.AddSingleton<VaultAccess>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true);

var app = builder.Build();

app.MapMcp("/mcp");

app.Logger.LogInformation(
    "Vaults loaded from {Path}: {Vaults} (default: {Default})",
    vaultsConfigPath, string.Join(", ", vaultConfig.Vaults.Keys), vaultConfig.DefaultVault);

app.Run();
