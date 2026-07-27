using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP coverage for the derived native schemas: that what
/// <see cref="VaultMcpServer.Tools.NativeToolArguments"/> exports from each tool's argument record
/// is what a client actually receives in <c>tools/list</c>, and that a real call to each of the
/// three natives behaves as it did when the schema was hand-authored and the arguments were
/// hand-walked.
///
/// <c>NativeToolSchemaTests</c> pins the same text against the tool objects; this class pins that it
/// survives the dispatcher's <c>tools/list</c> assembly and MCP's own serialization, which is the
/// only place a schema regression would actually reach a caller.
/// </summary>
[Collection("Program host tests")]
public sealed class NativeToolSchemaDispatchTests(NativeToolSchemaDispatchTests.Fixture fixture) : IClassFixture<NativeToolSchemaDispatchTests.Fixture>
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpClient AuthedClient(Fixture fixture, string token)
    {
        var client = fixture.CreateClient();
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

    private static async Task<JsonElement> SendAsync(Fixture fixture, string token, string json)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(json));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static string ContentText(JsonElement result) =>
        result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;

    private static (string Code, string? Argument) Refusal(JsonElement result)
    {
        using var doc = JsonDocument.Parse(ContentText(result));
        return (
            doc.RootElement.GetProperty("code").GetString()!,
            doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("argument", out var argument)
                ? argument.GetString()
                : null);
    }

    public static TheoryData<string, string> Pinned() => new()
    {
        { "mint_token", NativeToolSchemas.MintToken },
        { "request_elevation", NativeToolSchemas.RequestElevation },
        { "health_check", NativeToolSchemas.HealthCheck },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    public async Task ToolsList_NativeToolAdvertisesTheSameSchemaItAlwaysHas(string toolName, string expected)
    {
        var result = await SendAsync(fixture, Fixture.AdminToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tool = Assert.Single(
            result.GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == toolName);

        Assert.Equal(
            NativeToolSchemas.Canonical(expected),
            NativeToolSchemas.Canonical(tool.GetProperty("inputSchema")));
    }

    [Fact]
    public async Task MintToken_RealCall_StillMintsAgainstTheDerivedSchema()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal/Projects/Foo"],"op_class":["content:read"],"ttl":5}}}""";
        var result = await SendAsync(fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        Assert.False(string.IsNullOrEmpty(minted.RootElement.GetProperty("token").GetString()));
        Assert.Equal(5, minted.RootElement.GetProperty("ttlMinutes").GetInt32());
        Assert.Equal(["personal/Projects/Foo"], minted.RootElement.GetProperty("scopeRoots").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>
    /// The one thing the conversion could have broken on the wire: binding refuses with a
    /// <see cref="JsonException"/>, which is not a <c>{code, message, data?}</c> error. A caller must
    /// still see a coded refusal naming the argument, not a raw serializer message.
    /// </summary>
    [Fact]
    public async Task MintToken_RealCall_WrongTypedArgument_ReachesTheClientAsACodedRefusal()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":"personal","op_class":["content:read"]}}}""";
        var result = await SendAsync(fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(("invalid_argument", "scope_roots"), Refusal(result));
    }

    [Fact]
    public async Task RequestElevation_RealCall_StillResolvesItsNamedTarget()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"request_elevation","arguments":{"grant":"no-such-grant"}}}""";
        var result = await SendAsync(fixture, Fixture.ElevateToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        // not_found rather than a binding failure: "grant" bound off the record and got as far as
        // target resolution.
        Assert.Equal(("not_found", "grant"), Refusal(result));
    }

    [Fact]
    public async Task RequestElevation_RealCall_WrongTypedArgument_ReachesTheClientAsACodedRefusal()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"request_elevation","arguments":{"grant":5}}}""";
        var result = await SendAsync(fixture, Fixture.ElevateToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(("invalid_argument", "grant"), Refusal(result));
    }

    [Fact]
    public async Task HealthCheck_RealCall_StillTakesNoArguments()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var result = await SendAsync(fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var doc = JsonDocument.Parse(ContentText(result));
        Assert.False(doc.RootElement.GetProperty("isFatallyDegraded").GetBoolean());
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-native-schema-admin-token";
        public const string ElevateToken = "test-native-schema-elevate-token";

        public Fixture() : base("vault-mcp-native-schema-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, string> { ["personal"] = vaultRoot },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new { id = "admin", opClasses = new[] { "*" }, vaults = new[] { "*" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["*"] = ["*"] } },
                // request_elevation is gated by the per-grant opt-in rather than an op class, so its
                // argument handling is only reachable from a grant that carries it.
                [ElevateToken] = new { id = "elevate", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = [] }, canRequestElevation = true },
            }));

            // A throwaway tools/ directory with no scripts: this class asserts only native tools, and
            // an empty directory keeps the host out of the Degraded state a broken script would cause.
            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
