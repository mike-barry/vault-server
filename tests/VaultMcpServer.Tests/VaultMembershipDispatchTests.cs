using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end coverage for the dispatcher's authoritative vault-membership enforcement (Batch B
/// hardening): <see cref="Tools.ScriptToolDispatcher.CallToolAsync"/> re-checks
/// <c>grant.CanAccessVault</c> on the call's effective vault - the deserialized <c>vault</c>
/// argument when present, else the configured default vault - for every tool that takes a Vault
/// parameter. This is the check the middleware's best-effort body-peek 403 cannot be trusted to
/// cover: an omitted vault argument (which resolves to the default) sails past the peek, so these
/// cases exercise the dispatcher rather than the gate. Denials come back as normal MCP tool errors
/// (<c>isError</c>) carrying the shared <c>{code, message, data?}</c> JSON, matching the op-class
/// denial pattern and a tool script's own errors.
/// </summary>
[Collection("Program host tests")]
public sealed class VaultMembershipDispatchTests : IClassFixture<VaultMembershipDispatchTests.Fixture>
{
    private readonly Fixture _fixture;

    public VaultMembershipDispatchTests(Fixture fixture) => _fixture = fixture;

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

    private static async Task<(HttpStatusCode Status, bool IsError, string Text)> CallToolAsync(Fixture fixture, string? token, string toolCallJson)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(toolCallJson));
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, false, "");
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString() ?? "";
        return (response.StatusCode, isError, content);
    }

    [Fact]
    public async Task OmittedVault_ResolvingToOutOfMembershipDefault_IsDeniedAsToolError()
    {
        // personal-only grant, default vault is "test": omitting the vault sails past the
        // middleware peek (no vault named) and the dispatcher resolves the default "test", which
        // is outside the grant - the gap this check closes.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"note_tool","arguments":{"Name":"x"}}}""";
        var (status, isError, text) = await CallToolAsync(_fixture, Fixture.PersonalOnlyToken, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(isError, text);

        // The denial is the shared {code, message, data?} shape, not prose: a caller routes on
        // `code` whether the dispatcher or a tool script refused (Error Convention).
        using var error = JsonDocument.Parse(text);
        Assert.Equal("out_of_scope", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("test", error.RootElement.GetProperty("data").GetProperty("vault").GetString());
        Assert.Equal("note_tool", error.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }

    /// <summary>
    /// The denial must not become an enumeration surface: it names the vault the call effectively
    /// targeted, and nothing about what the grant does hold (its vault set or op classes). Mirrors
    /// the no-oracle posture the middleware's fixed 401 body keeps (LoopbackAuthMiddlewareTests).
    /// </summary>
    [Fact]
    public async Task VaultDenial_DoesNotDiscloseTheGrantsOwnVaultsOrOpClasses()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"note_tool","arguments":{"Name":"x"}}}""";
        var (_, _, text) = await CallToolAsync(_fixture, Fixture.PersonalOnlyToken, body);

        using var error = JsonDocument.Parse(text);
        var data = error.RootElement.GetProperty("data");
        Assert.False(data.TryGetProperty("vaults", out _));
        Assert.False(data.TryGetProperty("opClasses", out _));
        Assert.DoesNotContain("personal", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content:read", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultArgument_IsMatchedCaseInsensitively()
    {
        // Uppercase "Vault" key naming an in-membership vault, under a grant whose default vault
        // ("test") is out of membership: the call succeeds only if the dispatcher matched the
        // argument case-insensitively (else it would fall back to the default and deny).
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"note_tool","arguments":{"Name":"x","Vault":"personal"}}}""";
        var (status, isError, text) = await CallToolAsync(_fixture, Fixture.PersonalOnlyToken, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(isError, text);
    }

    [Fact]
    public async Task ExplicitInMembershipVault_IsAllowed()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"note_tool","arguments":{"Name":"x","Vault":"test"}}}""";
        var (status, isError, text) = await CallToolAsync(_fixture, Fixture.BroadToken, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(isError, text);
    }

    [Fact]
    public async Task FloorGrant_VaultBearingTool_IsDenied()
    {
        // An unknown token floors to minimum-access on loopback; a vault-bearing tool is denied as
        // a tool error. The floor has neither op classes nor vaults, so the op-class check happens
        // to reject it first - either way it never reaches the script, which is the point.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"note_tool","arguments":{"Name":"x"}}}""";
        var (status, isError, text) = await CallToolAsync(_fixture, "some-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(isError, text);
        using var error = JsonDocument.Parse(text);
        Assert.Equal("authority_required", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FloorGrant_HealthCheck_StillPasses()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var (status, isError, text) = await CallToolAsync(_fixture, "some-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(isError, text);
    }

    [Fact]
    public async Task FloorGrant_RequestElevation_IsNotDeniedForVaultScope()
    {
        // request_elevation names no vault, so it is out of scope of the membership check (it is a
        // native, op-class-exempt tool) - it reaches its own logic rather than a vault denial.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"request_elevation","arguments":{"grant_id":"admin"}}}""";
        var (status, _, text) = await CallToolAsync(_fixture, "some-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("not scoped to vault", text);
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string BroadToken = "test-membership-broad-token";
        public const string PersonalOnlyToken = "test-membership-personal-only-token";

        public Fixture() : base("vault-mcp-membership-tests-")
        {
            var personalRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(personalRoot);
            var testRoot = Path.Combine(TempDir, "test");
            Directory.CreateDirectory(testRoot);

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "test",
                vaults = new Dictionary<string, object>{ ["personal"] = new { path = personalRoot }, ["test"] = new { path = testRoot } },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [BroadToken] = new { id = "membership-broad", opClasses = new[] { "content:read" }, vaults = new[] { "personal", "test" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" }, ["test"] = new[] { "*" } } },
                [PersonalOnlyToken] = new { id = "membership-personal-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);
            // A vault-bearing script tool that echoes rather than touching the filesystem - the
            // membership check runs before the script does, so denied cases never invoke it, and
            // allowed cases just need it to run cleanly.
            File.WriteAllText(Path.Combine(toolsDir, "note_tool.ps1"), """
                <#
                .SYNOPSIS
                Echoes a note operation for membership tests.
                .PARAMETER Name
                An arbitrary label.
                .PARAMETER Vault
                The vault to operate in.
                .NOTES
                OpClass: content:read
                #>
                param([Parameter(Mandatory=$true)] [string]$Name, [string]$Vault)
                Write-Output "note $Name in $Vault"
                """);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
