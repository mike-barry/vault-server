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
        { "read", NativeToolSchemas.Read },
        { "glob", NativeToolSchemas.Glob },
        { "grep", NativeToolSchemas.Grep },
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
        Assert.Equal(5, minted.RootElement.GetProperty("ttl_minutes").GetInt32());
        Assert.Equal(["personal/Projects/Foo"], minted.RootElement.GetProperty("scope_roots").EnumerateArray().Select(v => v.GetString()));
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
        Assert.False(doc.RootElement.GetProperty("is_fatally_degraded").GetBoolean());
    }

    // --- Generic conformance, over whatever the server advertises -------------------------
    //
    // Everything above names its tools by hand. These three read the tool set out of
    // tools/list instead, so a tool added later is covered on registration rather than when
    // someone remembers to extend an InlineData list.

    private static async Task<Dictionary<string, JsonElement>> AdvertisedAsync(Fixture fixture) =>
        (await SendAsync(fixture, Fixture.AdminToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))
            .GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("inputSchema").Clone());

    private static string[] RequiredOf(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(a => a.GetString()!).ToArray()
            : [];

    /// <summary>
    /// One fully valid call per tool that advertises required arguments - the one thing
    /// <see cref="AdvertisedRequiredArguments_AreEnforced"/> cannot derive, since only the tool's
    /// own tests know what a good call looks like. A tool with nothing in <c>required</c> needs no
    /// entry; <see cref="EveryToolWithRequiredArguments_HasAValidCall"/> keeps that honest.
    /// </summary>
    public static readonly Dictionary<string, (string Token, string Arguments)> ValidCalls = new()
    {
        ["mint_token"] = (Fixture.AdminToken, """{"scope_roots":["personal/Projects/Foo"],"op_class":["content:read"],"ttl":5}"""),
        ["read"] = (Fixture.AdminToken, """{"path":"Note.md","vault":"personal"}"""),
        ["glob"] = (Fixture.AdminToken, """{"pattern":"*.md","vault":"personal"}"""),
        ["grep"] = (Fixture.AdminToken, """{"pattern":"widget","vault":"personal"}"""),
    };

    /// <summary>
    /// Every advertised tool is pinned. <see cref="Pinned"/> is a hand-written list, so without this
    /// a fourth native would ship with no schema assertion at all and nothing would say so.
    /// </summary>
    [Fact]
    public async Task EveryAdvertisedTool_HasAPinnedSchema()
    {
        var advertised = await AdvertisedAsync(fixture);
        Assert.Equal(
            advertised.Keys.OrderBy(n => n, StringComparer.Ordinal),
            Pinned().Select(row => (string)row[0]!).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>The same guard for the sample payloads the conformance check runs on.</summary>
    [Fact]
    public async Task EveryToolWithRequiredArguments_HasAValidCall()
    {
        var advertised = await AdvertisedAsync(fixture);
        Assert.Equal(
            advertised.Where(t => RequiredOf(t.Value).Length > 0).Select(t => t.Key).OrderBy(n => n, StringComparer.Ordinal),
            ValidCalls.Keys.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>required</c> must mean what it says: omitting an advertised-required argument has to
    /// produce that tool's coded refusal naming it. The failure this exists for is silent in both
    /// directions - an optional argument declared without a C# default is advertised required while
    /// the tool still substitutes a default and never refuses, so the schema promises a constraint
    /// nothing enforces and no existing assertion notices.
    ///
    /// Only that direction is checked. The inverse - a tool refusing on something it never
    /// advertised - is indistinguishable from the conditional requiredness the schema is not
    /// expected to express (<c>request_elevation</c> takes <c>grant</c> OR
    /// <c>scope_roots</c>+<c>op_class</c>, so neither is required on its own), and flagging it here
    /// would make a legitimate shape fail.
    /// </summary>
    [Fact]
    public async Task AdvertisedRequiredArguments_AreEnforced()
    {
        var problems = new List<string>();

        foreach (var (name, schema) in await AdvertisedAsync(fixture))
        {
            var required = RequiredOf(schema);
            if (required.Length == 0)
                continue;

            var (token, argumentsJson) = ValidCalls[name];
            using var valid = JsonDocument.Parse(argumentsJson);

            foreach (var argument in required)
            {
                var withoutIt = valid.RootElement.EnumerateObject()
                    .Where(p => p.Name != argument)
                    .ToDictionary(p => p.Name, p => p.Value);

                var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":ARGS}}"""
                    .Replace("TOOL", name)
                    .Replace("ARGS", JsonSerializer.Serialize(withoutIt));
                var result = await SendAsync(fixture, token, body);

                if (!(result.TryGetProperty("isError", out var isError) && isError.GetBoolean()))
                    problems.Add($"{name}: '{argument}' is advertised required, but omitting it succeeds");
                else if (Refusal(result).Argument is var named && named != argument)
                    problems.Add($"{name}: '{argument}' is advertised required, but omitting it refuses on '{named ?? "(unnamed)"}'");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
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
                vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot } },
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
