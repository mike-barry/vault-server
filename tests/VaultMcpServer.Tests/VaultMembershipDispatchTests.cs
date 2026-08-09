using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers over HTTP that the dispatcher enforces vault membership for every tool that takes a
/// Vault parameter. <see cref="Tools.ScriptToolDispatcher.CallToolAsync"/> re-checks
/// <c>grant.CanAccessVault</c> against the call's effective vault: the deserialized <c>vault</c>
/// argument when present, otherwise the configured default vault.
/// </summary>
/// <remarks>
/// The dispatcher is the only site that answers this. The bearer gate authenticates and passes
/// every authenticated call on, so a call naming a vault outside the grant and one that omits the
/// argument and defaults to the same vault are refused in one place and one shape.
///
/// A denial arrives as a normal MCP tool error (<c>isError</c>) carrying the shared
/// <c>{code, message, data?}</c> JSON, the same shape as an op-class denial or a tool script's own
/// error.
/// </remarks>
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
        // The grant holds "personal" only. Omitting the vault argument leaves the call targeting
        // the default vault "test", which the dispatcher resolves and finds outside the grant.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"note_tool","arguments":{"Name":"x"}}}""";
        var (status, isError, text) = await CallToolAsync(_fixture, Fixture.PersonalOnlyToken, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(isError, text);

        // The denial uses the shared {code, message, data?} shape rather than prose, so a caller
        // routes on the code whether the dispatcher or a tool script refused.
        using var error = JsonDocument.Parse(text);
        Assert.Equal("out_of_scope", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("test", error.RootElement.GetProperty("data").GetProperty("vault").GetString());
        Assert.Equal("note_tool", error.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }

    /// <summary>
    /// Verifies that a denial names only the vault the call effectively targeted and says nothing
    /// about what the grant holds. A caller must not be able to enumerate a grant's vaults or op
    /// classes from refusals.
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
        // The grant does not hold the default vault "test", so the call can only succeed if the
        // dispatcher matched the uppercase "Vault" key against the vault argument
        // case-insensitively. Otherwise it falls back to the default and denies.
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
        // An unrecognized token floors to minimum access on loopback. The floor grant holds
        // neither op classes nor vaults, so the op-class check rejects the call before the
        // membership check runs. Either way the script is never invoked.
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
        // request_elevation is a native tool that is exempt from the op-class floor and takes no
        // vault parameter, so the membership check does not apply and the call reaches the tool's
        // own logic.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"request_elevation","arguments":{"grant_id":"admin"}}}""";
        var (status, _, text) = await CallToolAsync(_fixture, "some-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("not scoped to vault", text);
    }

    /// <summary>
    /// Hosts the server over two empty vaults, <c>personal</c> and <c>test</c>, with <c>test</c> as
    /// the default vault, and registers one script tool that takes a Vault parameter.
    /// </summary>
    public sealed class Fixture : ProgramHostFixtureBase
    {
        /// <summary>A grant holding both vaults.</summary>
        public const string BroadToken = "test-membership-broad-token";

        /// <summary>A grant holding <c>personal</c> alone, so the default vault <c>test</c> lies
        /// outside it and an omitted vault argument is denied.</summary>
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
            // A script tool with a Vault parameter that echoes its arguments instead of touching
            // the filesystem. The membership check runs before the script, so denied cases never
            // invoke it and allowed cases only need it to run cleanly.
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
