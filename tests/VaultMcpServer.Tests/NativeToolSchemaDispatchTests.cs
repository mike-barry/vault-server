using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the derived native schemas over HTTP: that what
/// <see cref="VaultShared.Tools.NativeToolArguments"/> exports from each tool's argument record is
/// what a client receives in <c>tools/list</c>, and that a real call to each declared tool binds
/// against that same schema rather than a second reading of it.
/// </summary>
/// <remarks>
/// <c>NativeToolSchemaTests</c> pins the same text against the tool objects. These tests pin that it
/// survives the dispatcher's <c>tools/list</c> assembly and MCP's own serialization, which is where
/// a schema regression would reach a caller.
///
/// This fixture's vault names no frontmatter declaration, so it also covers what <c>edit</c> looks
/// like without one: still advertised with its ordinary argument surface, and its <c>entry</c>
/// argument carrying no variant branches. <c>EditDispatchTests</c> covers the loaded case.
/// </remarks>
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
        { "edit", NativeToolSchemas.Edit },
        { "create", NativeToolSchemas.Create },
        { "move", NativeToolSchemas.Move },
        { "delete", NativeToolSchemas.Delete },
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
    /// Verifies that a wrongly typed argument reaches the client as a coded refusal naming the
    /// argument. Binding fails with a <see cref="JsonException"/>, which is not a
    /// <c>{code, message, data?}</c> error, so the raw serializer message must not be what a caller
    /// sees.
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
        // not_found rather than a binding failure, so "grant" bound successfully and the call got as
        // far as resolving the target.
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
    // The tests above name their tools by hand. These three read the tool set out of tools/list
    // instead, so a tool added later is covered as soon as it is registered.

    private static async Task<Dictionary<string, JsonElement>> AdvertisedAsync(Fixture fixture) =>
        (await SendAsync(fixture, Fixture.AdminToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))
            .GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("inputSchema").Clone());

    private static string[] RequiredOf(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(a => a.GetString()!).ToArray()
            : [];

    /// <summary>
    /// Holds one fully valid call per tool that advertises required arguments.
    /// <see cref="AdvertisedRequiredArguments_AreEnforced"/> cannot derive these, because only the
    /// tool itself defines what a good call looks like. A tool with nothing in <c>required</c> needs
    /// no entry, and <see cref="EveryToolWithRequiredArguments_HasAValidCall"/> checks that the two
    /// sets agree.
    /// </summary>
    public static readonly Dictionary<string, (string Token, string Arguments)> ValidCalls = new()
    {
        ["mint_token"] = (Fixture.AdminToken, """{"scope_roots":["personal/Projects/Foo"],"op_class":["content:read"],"ttl":5}"""),
        ["read"] = (Fixture.AdminToken, """{"path":"Note.md","vault":"personal"}"""),
        ["glob"] = (Fixture.AdminToken, """{"pattern":"*.md","vault":"personal"}"""),
        ["grep"] = (Fixture.AdminToken, """{"pattern":"widget","vault":"personal"}"""),
        ["edit"] = (Fixture.AdminToken, """{"path":"Note.md","vault":"personal","body":{"find":"a","replace":"b"}}"""),
        // The three mutating tools' entries are never sent as-is. This suite only sends them with
        // one required argument removed, and each of those refuses in the argument phase, before
        // the lock and before anything reaches the filesystem.
        ["create"] = (Fixture.AdminToken, """{"path":"New.md","kind":"note","vault":"personal","content":""}"""),
        ["move"] = (Fixture.AdminToken, """{"path":"Note.md","destination":"Moved.md","vault":"personal"}"""),
        ["delete"] = (Fixture.AdminToken, """{"path":"Note.md","kind":"note","vault":"personal"}"""),
    };

    /// <summary>
    /// Verifies that every advertised tool appears in <see cref="Pinned"/>. That list is
    /// hand-written, so without this check a newly declared tool would ship with no schema
    /// assertion and nothing would report it.
    /// </summary>
    [Fact]
    public async Task EveryAdvertisedTool_HasAPinnedSchema()
    {
        var advertised = await AdvertisedAsync(fixture);
        Assert.Equal(
            advertised.Keys.OrderBy(n => n, StringComparer.Ordinal),
            Pinned().Select(row => (string)row[0]!).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// Verifies that <see cref="ValidCalls"/> holds an entry for exactly those advertised tools that
    /// declare required arguments.
    /// </summary>
    [Fact]
    public async Task EveryToolWithRequiredArguments_HasAValidCall()
    {
        var advertised = await AdvertisedAsync(fixture);
        Assert.Equal(
            advertised.Where(t => RequiredOf(t.Value).Length > 0).Select(t => t.Key).OrderBy(n => n, StringComparer.Ordinal),
            ValidCalls.Keys.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// Verifies that omitting an argument the schema advertises as required produces that tool's
    /// coded refusal naming it.
    /// </summary>
    /// <remarks>
    /// The failure this catches is silent: an optional argument declared without a C# default is
    /// advertised as required while the tool still substitutes a default and never refuses, so the
    /// schema promises a constraint nothing enforces.
    ///
    /// Only that direction is checked. A tool refusing on an argument it never advertised as
    /// required is indistinguishable from conditional requiredness, which the schema is not expected
    /// to express - <c>request_elevation</c> takes either <c>grant</c> or <c>scope_roots</c> with
    /// <c>op_class</c>, so neither is required on its own - and flagging it would fail a legitimate
    /// shape.
    /// </remarks>
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
                // argument handling is only reachable from a grant that sets canRequestElevation.
                [ElevateToken] = new { id = "elevate", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = [] }, canRequestElevation = true },
            }));

            // A throwaway tools/ directory with no scripts. These tests assert only native tools,
            // and an empty directory keeps the host out of the Degraded state a broken script would
            // cause.
            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
