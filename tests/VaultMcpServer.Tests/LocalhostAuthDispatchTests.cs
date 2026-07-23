using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP coverage for the Batch A localhost auth model wired through the real host: the
/// well-known selector token resolving to the configured default named grant, and blank/unknown
/// tokens degrading to the minimum-access floor (health_check still works; vault content is
/// denied). Boots Program with a token-less named <c>admin</c> grant and
/// <c>LocalhostAuth__SelectorToken</c>/<c>DefaultGrantId</c> config, so the whole
/// config -> LocalhostAuthOptions -> GrantStore.ResolveLocalhostGrant -> BearerGrantMiddleware
/// path is exercised. Own host per class (a fresh GrantStore).
/// </summary>
[Collection("Program host tests")]
public sealed class LocalhostAuthDispatchTests : IClassFixture<LocalhostAuthDispatchTests.Fixture>
{
    private readonly Fixture _fixture;

    public LocalhostAuthDispatchTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpClient AuthedClient(Fixture fixture, string? token)
    {
        var client = fixture.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;
        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    private static async Task<(HttpStatusCode Status, JsonElement? Result)> CallToolAsync(Fixture fixture, string? token, string toolCallJson)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(toolCallJson));
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        return (response.StatusCode, doc.RootElement.GetProperty("result").Clone());
    }

    [Fact]
    public async Task SelectorToken_ResolvesToDefaultAdminGrant_CanMint()
    {
        // mint_token requires ManageGrants; the selector binding to admin is what grants it.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"]}}}""";
        var (status, result) = await CallToolAsync(_fixture, Fixture.SelectorToken, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task UnknownToken_FloorsToMinimum_HealthCheckWorks()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var (status, result) = await CallToolAsync(_fixture, "some-random-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task UnknownToken_FloorsToMinimum_ManageGrantsToolDenied()
    {
        // The minimum floor has no op classes, so mint_token (ManageGrants) is a tool error.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"]}}}""";
        var (status, result) = await CallToolAsync(_fixture, "some-random-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!.Value.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MinimumFloor_VaultNamingToolIsRejectedWith403()
    {
        // The minimum floor has no vaults, so a tools/call naming one is rejected at the gate.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"vault":"personal","Name":"x"}}}""";
        var (status, _) = await CallToolAsync(_fixture, "some-random-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task BlankToken_FloorsToMinimum_HealthCheckWorks()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var (status, result) = await CallToolAsync(_fixture, null, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string SelectorToken = "test-localhost-selector";

        public Fixture() : base("vault-mcp-localhost-auth-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, string> { ["personal"] = vaultRoot },
            }));

            // A token-less named "admin" grant (the default-binding target) plus one ordinary
            // read-only token row, exercising both kinds side by side.
            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["admin"] = new { named = true, id = "admin", opClasses = new[] { "*" }, vaults = new[] { "*" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["*"] = new[] { "*" } } },
                ["a-real-readonly-token"] = new { id = "read-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>() } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);
            File.WriteAllText(Path.Combine(toolsDir, "greet.ps1"), """
                <#
                .SYNOPSIS
                Greets someone by name.
                .PARAMETER Name
                Who to greet.
                .NOTES
                OpClass: content:write
                #>
                param([Parameter(Mandatory=$true)] [string]$Name)
                Write-Output "hello $Name"
                """);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
            SetEnvironmentVariable("LocalhostAuth__SelectorToken", SelectorToken);
            SetEnvironmentVariable("LocalhostAuth__DefaultGrantId", "admin");
        }
    }
}
