using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// Boots the real host (Program) against a temp vaults.json/grants.json and drives it
/// over HTTP through TestServer, proving the bearer gate actually authenticates and
/// scopes requests rather than just unit-testing GrantStore in isolation.
/// </summary>
[Collection("Program host tests")]
public sealed class BearerGrantMiddlewareTests : IClassFixture<BearerGrantMiddlewareTests.Fixture>
{
    private readonly Fixture _fixture;

    public BearerGrantMiddlewareTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MissingAuthorizationHeader_Returns401()
    {
        using var response = await _fixture.CreateClient().PostAsync("/mcp", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownToken_Returns401()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        using var response = await client.PostAsync("/mcp", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KnownToken_IsNotRejectedByTheGate()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ToolCallNamingOutOfScopeVault_Returns403()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rename_note","arguments":{"vault":"not-configured","from":"a","to":"b"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ToolCallNamingInScopeVault_IsNotRejectedByTheGate()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_note","arguments":{"vault":"personal","path":"a.md"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public const string AdminToken = "test-admin-token";
        public const string ReadOnlyToken = "test-readonly-token";

        private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-middleware-tests-").FullName;

        /// <summary>
        /// Program.cs reads VaultsConfigPath/GrantsConfigPath from IConfiguration before
        /// builder.Build(), so a ConfigureWebHost/ConfigureAppConfiguration override lands
        /// too late - WebApplicationFactory doesn't guarantee it's applied before the
        /// entry point's own top-level code runs. Environment variables are folded into
        /// WebApplicationBuilder.Configuration synchronously inside CreateBuilder() itself,
        /// so setting them here (before the host is lazily built on first client/server
        /// access) reliably overrides both paths.
        /// </summary>
        public Fixture()
        {
            var vaultRoot = Path.Combine(_tempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(_tempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, string> { ["personal"] = vaultRoot },
            }));

            var grantsPath = Path.Combine(_tempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new { id = "admin", opClasses = new[] { "Read", "Write" }, vaults = new[] { "personal" } },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "Read" }, vaults = new[] { "personal" } },
            }));

            Environment.SetEnvironmentVariable("VaultsConfigPath", vaultsPath);
            Environment.SetEnvironmentVariable("GrantsConfigPath", grantsPath);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("VaultsConfigPath", null);
                Environment.SetEnvironmentVariable("GrantsConfigPath", null);
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
