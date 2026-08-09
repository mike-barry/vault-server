using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers script tools end to end. Boots the real host against a temporary tools/ folder and drives
/// tools/list and tools/call over HTTP, so script discovery, schema exposure, execution and
/// per-tool op-class enforcement are exercised together rather than each in isolation.
/// </summary>
[Collection("Program host tests")]
public sealed class ScriptToolDispatcherTests : IClassFixture<ScriptToolDispatcherTests.Fixture>
{
    private readonly Fixture _fixture;

    public ScriptToolDispatcherTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Builds a <c>tools/call</c> request body. Serialized rather than written as a literal, so an
    /// argument name or value the test supplies - a file path, a theory case - is escaped rather
    /// than pasted into JSON.
    /// </summary>
    /// <param name="toolName">The tool to call.</param>
    /// <param name="arguments">The call's arguments, all of them strings.</param>
    /// <returns>The JSON-RPC request body.</returns>
    private static string ToolCallBody(string toolName, Dictionary<string, string> arguments) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = toolName, arguments },
        });

    /// <summary>
    /// Builds the same request body with arguments that need not be strings, so the encoding tests
    /// send a real JSON array or object rather than a pre-stringified stand-in.
    /// </summary>
    /// <param name="toolName">The tool to call.</param>
    /// <param name="arguments">The call's arguments, of any JSON-serializable type.</param>
    /// <returns>The JSON-RPC request body.</returns>
    private static string StructuredToolCallBody(string toolName, Dictionary<string, object?> arguments) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = toolName, arguments },
        });

    private static HttpClient AuthedClient(Fixture fixture, string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Streamable HTTP requires both media types, and answers 406 without them. The server
        // replies as an SSE stream when text/event-stream is accepted, so a response has to go
        // through ExtractJsonRpcPayload rather than straight into JsonDocument.Parse.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    /// <summary>Extracts the JSON-RPC payload from a response body that may be a single SSE
    /// <c>data:</c> event rather than a plain JSON object.</summary>
    /// <param name="body">The raw response body.</param>
    /// <returns>The JSON-RPC payload text.</returns>
    /// <exception cref="InvalidOperationException">The body looks like SSE but carries no
    /// <c>data:</c> line.</exception>
    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;

        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    [Fact]
    public async Task ToolsList_IncludesTheFixtureToolWithItsSchema()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(body));
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var greet = Assert.Single(tools, t => t.GetProperty("name").GetString() == "greet");
        Assert.Equal("Greets someone by name.", greet.GetProperty("description").GetString());

        // The whole schema node, not just the presence of the property. A listing that lost the
        // derived type, the help text or the mandatory marking on the way over the wire would
        // still carry a "Name" key, so presence alone does not show the schema arrived intact.
        var inputSchema = greet.GetProperty("inputSchema");
        Assert.Equal("object", inputSchema.GetProperty("type").GetString());
        var nameProperty = inputSchema.GetProperty("properties").GetProperty("Name");
        Assert.Equal("string", nameProperty.GetProperty("type").GetString());
        Assert.Equal("Who to greet.", nameProperty.GetProperty("description").GetString());
        var required = inputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "Name" }, required);
    }

    [Fact]
    public async Task ToolsCall_AdminGrant_ExecutesTheScriptAndReturnsItsOutput()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString();
        Assert.Equal("hello Mike", content);
    }

    [Fact]
    public async Task ToolsCall_ReadOnlyGrantCallingWriteTool_IsRejectedAsToolError()
    {
        using var client = AuthedClient(_fixture, Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        // The floor denial carries the shared {code, message, data?} shape, with the same code the
        // in-script key assertion throws, so a caller handles both layers in one branch.
        var errorText = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        Assert.Equal("authority_required", error.RootElement.GetProperty("code").GetString());
        // The payload's names are snake_case, because they come from the same serializer options
        // the argument binder uses.
        Assert.Equal("content:write", error.RootElement.GetProperty("data").GetProperty("required_op_class").GetString());
        Assert.Equal("greet", error.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }

    /// <summary>
    /// Verifies that an op-class denial names the class the tool required, which a caller needs in
    /// order to request elevation, and says nothing about the classes the caller's grant holds.
    /// </summary>
    [Fact]
    public async Task ToolsCall_OpClassDenial_DoesNotDiscloseTheGrantsHeldClasses()
    {
        using var client = AuthedClient(_fixture, Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var errorText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        var data = error.RootElement.GetProperty("data").GetRawText();
        Assert.DoesNotContain("content:read", data, StringComparison.Ordinal);
        Assert.DoesNotContain("read-only", data, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the <c>tools/</c> file watcher is wired into the real host: a script written to
    /// disk after startup appears in <c>tools/list</c> without a restart, once the watcher's
    /// debounce window has elapsed.
    /// </summary>
    [Fact]
    public async Task ToolsList_AfterAddingScriptFileOnDisk_ReflectsTheNewToolOnceDebounced()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);

        await File.WriteAllTextAsync(Path.Combine(_fixture.ToolsDir, "farewell.ps1"), """
            <#
            .SYNOPSIS
            Says goodbye.
            .NOTES
            OpClass: content:read
            #>
            Write-Output "bye"
            """);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        List<string> names = [];
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(body));
            names = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
            if (names.Contains("farewell"))
                break;
            await Task.Delay(200);
        }

        Assert.Contains("farewell", names);
        Assert.Contains("greet", names);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsRejectedAsToolError()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        // An unknown name is refused before any script runs and still carries the shared shape:
        // not_found over the caller's own tool name, disclosing nothing about the tool set.
        var errorText = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        Assert.Equal("not_found", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("no_such_tool", error.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }

    /// <summary>
    /// Verifies that the dispatcher's argument-name check matches case-insensitively, as
    /// PowerShell's binder does. A name that would have bound must not be refused here, or the check
    /// is stricter than the binder it stands in front of.
    /// </summary>
    [Fact]
    public async Task ToolsCall_KnownArgumentInNonMatchingCase_StillDispatches()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        // The param() block (and so the advertised schema) spells this "Name".
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"nAmE":"Mike"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Equal("hello Mike", result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString());
    }

    /// <summary>
    /// Verifies that an argument naming no advertised parameter is refused by the dispatcher as
    /// <c>invalid_argument</c>. Left to reach the runspace, <c>[CmdletBinding()]</c> would refuse it
    /// with a raw PowerShell binding message outside the shared error shape.
    /// </summary>
    [Fact]
    public async Task ToolsCall_UnknownArgumentName_IsRejectedAsInvalidArgument()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike","Nmae":"typo"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        var errorText = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        Assert.Equal("invalid_argument", error.RootElement.GetProperty("code").GetString());
        var data = error.RootElement.GetProperty("data");
        Assert.Equal("greet", data.GetProperty("tool").GetString());
        Assert.Equal("Nmae", data.GetProperty("param").GetString());
    }

    /// <summary>
    /// Verifies that PowerShell's common parameters, which <c>[CmdletBinding()]</c> makes bindable
    /// on every tool script, are refused by the same check as any other unadvertised name. A tool's
    /// param() block is its entire client-facing argument surface.
    /// </summary>
    /// <param name="commonParameter">The common parameter name to send.</param>
    [Theory]
    [InlineData("Verbose")]
    [InlineData("ErrorAction")]
    [InlineData("OutVariable")]
    public async Task ToolsCall_CommonParameter_IsRefusedByTheSameCheck(string commonParameter)
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var body = ToolCallBody("greet", new() { ["Name"] = "Mike", [commonParameter] = "Continue" });
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        var errorText = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        Assert.Equal("invalid_argument", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(commonParameter, error.RootElement.GetProperty("data").GetProperty("param").GetString());
    }

    /// <summary>
    /// Verifies that the refusal happens before the runner is invoked, not after the script has had
    /// its effect. The same call without the unadvertised argument writes a marker file, so the
    /// absence of that file on the refused call shows the script never ran.
    /// </summary>
    [Fact]
    public async Task ToolsCall_UnknownArgumentName_RefusesBeforeTheScriptRuns()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var refusedMarker = Path.Combine(_fixture.MarkerDir, $"marker-refused-{Guid.NewGuid():N}.txt");
        var allowedMarker = Path.Combine(_fixture.MarkerDir, $"marker-allowed-{Guid.NewGuid():N}.txt");

        using var refused = await client.PostAsync("/mcp", JsonBody(ToolCallBody("mark", new() { ["Marker"] = refusedMarker, ["Bogus"] = "x" })));
        using var refusedDoc = JsonDocument.Parse(ExtractJsonRpcPayload(await refused.Content.ReadAsStringAsync()));
        Assert.True(refusedDoc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(File.Exists(refusedMarker));

        // Control: the identical call without the unadvertised name does run the script, so the
        // assertion above is about the check rather than about the fixture script doing nothing.
        using var allowed = await client.PostAsync("/mcp", JsonBody(ToolCallBody("mark", new() { ["Marker"] = allowedMarker })));
        using var allowedDoc = JsonDocument.Parse(ExtractJsonRpcPayload(await allowed.Content.ReadAsStringAsync()));
        var allowedResult = allowedDoc.RootElement.GetProperty("result");
        Assert.False(allowedResult.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.True(File.Exists(allowedMarker));
    }

    /// <summary>
    /// Calls the <c>shape</c> fixture tool and returns its report. The report is the script's own
    /// output, so the encoding tests assert on what the script parsed rather than on what the
    /// dispatcher put in the splat.
    /// </summary>
    /// <param name="arguments">The arguments to send, of any JSON-serializable type.</param>
    /// <returns>The script's report, parsed.</returns>
    private async Task<JsonElement> CallShapeAsync(Dictionary<string, object?> arguments)
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody(StructuredToolCallBody("shape", arguments)));
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), content);
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    /// <summary>
    /// Verifies that a real JSON array sent for a parameter whose side-car declares
    /// <c>type: "array"</c> reaches the <c>[string]</c> script parameter as JSON text, which the
    /// script's own <c>ConvertFrom-Json -NoEnumerate</c> parses back into the same array. Without
    /// the encoding the splat hands PowerShell an <c>object[]</c>, which stringifies into a
    /// <c>[string]</c> parameter as "System.Object[]" and does not parse.
    /// </summary>
    [Fact]
    public async Task ToolsCall_StructuredArrayForSideCarArrayParameter_ReachesTheScriptAsParseableJson()
    {
        var report = await CallShapeAsync(new()
        {
            ["items"] = new[] { new { name = "first" }, new { name = "second" } },
        });

        Assert.True(report.GetProperty("itemsIsArray").GetBoolean());
        Assert.Equal(2, report.GetProperty("itemsCount").GetInt32());
        Assert.Equal("first", report.GetProperty("itemsFirstName").GetString());
        Assert.Equal("""[{"name":"first"},{"name":"second"}]""", report.GetProperty("itemsRaw").GetString());
    }

    /// <summary>
    /// Verifies that a caller who already encoded its payload is not encoded again. Only a
    /// structured arrival value is serialized; a JSON string passes through verbatim. Encoding it a
    /// second time would hand the script a JSON string literal where it expects an array, which
    /// parses as a bare string and is refused.
    /// </summary>
    [Fact]
    public async Task ToolsCall_AlreadyEncodedStringForSideCarArrayParameter_IsNotEncodedAgain()
    {
        // Deliberately the same payload, character for character, that
        // ToolsCall_StructuredArrayForSideCarArrayParameter_ReachesTheScriptAsParseableJson sends
        // in structured form and pins the arrival text of. Holding the two to one literal is what
        // shows a caller may send either form and the script sees no difference; change one and
        // the pair stops saying that.
        const string encoded = """[{"name":"first"},{"name":"second"}]""";
        var report = await CallShapeAsync(new() { ["items"] = encoded });

        // The same characters the caller sent, not a JSON string literal wrapping them.
        Assert.Equal(encoded, report.GetProperty("itemsRaw").GetString());
        Assert.True(report.GetProperty("itemsIsArray").GetBoolean());
        Assert.Equal(2, report.GetProperty("itemsCount").GetInt32());
        Assert.Equal("first", report.GetProperty("itemsFirstName").GetString());
    }

    /// <summary>
    /// Verifies that a JSON number for a numeric parameter reaches the splat as an integral CLR
    /// type rather than a double, against the real host.
    /// </summary>
    /// <remarks>
    /// The value has to exceed 2^53. A whole-valued double binds to <c>[long]</c> without complaint
    /// and stringifies to the same text, so a small integer would pass even if the conversion
    /// widened everything to double. 2^53+1 is the smallest value a double cannot hold exactly: the
    /// script would receive 9007199254740992 for an argument the client sent as 9007199254740993.
    ///
    /// No current tool declares a numeric parameter, so this covers the contract
    /// <c>MapJsonType</c> advertises - <c>[int]</c> and <c>[long]</c> as JSON "integer" - rather
    /// than a shipped surface.
    /// </remarks>
    [Fact]
    public async Task ToolsCall_JsonNumberForANumericParameter_ReachesTheScriptAsAnExactInteger()
    {
        const long beyondDoublePrecision = 9007199254740993L;
        var report = await CallShapeAsync(new() { ["count"] = beyondDoublePrecision });

        Assert.Equal("Int64", report.GetProperty("countType").GetString());
        Assert.Equal("9007199254740993", report.GetProperty("countValue").GetString());
    }

    /// <summary>Verifies that an object-typed side-car encodes a structured object the same way an
    /// array-typed one does. What matters is that the declared type is not "string".</summary>
    [Fact]
    public async Task ToolsCall_StructuredObjectForSideCarObjectParameter_ReachesTheScriptAsParseableJson()
    {
        var report = await CallShapeAsync(new()
        {
            ["payload"] = new { message = "recorded" },
        });

        Assert.Equal("recorded", report.GetProperty("payloadMessage").GetString());
    }

    /// <summary>
    /// Verifies the two non-encoding cases together: a parameter with no side-car, and one whose
    /// side-car declares <c>type: "string"</c>. A string argument arrives as itself in both.
    /// </summary>
    [Fact]
    public async Task ToolsCall_ParametersWithNoSideCarOrAStringSideCar_PassStringsThroughUnchanged()
    {
        var report = await CallShapeAsync(new()
        {
            ["plain"] = """[{"name":"first"}]""",
            ["declared_string"] = "just text",
        });

        // A JSON-looking string against a parameter with no side-car is still just a string. The
        // dispatcher has no schema to read, so it neither encodes nor decodes it.
        Assert.Equal("""[{"name":"first"}]""", report.GetProperty("plain").GetString());
        Assert.Equal("just text", report.GetProperty("declaredString").GetString());
    }

    /// <summary>
    /// Verifies that a payload round-trips verbatim through the encoder, characters and all. The
    /// test payload carries non-ASCII text plus the characters <c>System.Text.Json</c>'s default
    /// encoder escapes for HTML safety (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>+</c>,
    /// <c>'</c>), any of which can appear in a vault path or a frontmatter value.
    /// </summary>
    /// <remarks>
    /// The relaxed encoder writes those characters as themselves. The default encoder would still
    /// produce valid JSON that parses back to the same value, but the payload text - which a tool
    /// echoes into its error data and its logs - would be a run of escapes.
    /// </remarks>
    [Fact]
    public async Task ToolsCall_PayloadWithNonAsciiAndDefaultEscapeSetCharacters_RoundTripsVerbatim()
    {
        const string awkward = "café — 日本語 <tag> & 'quoted' a+b";
        var report = await CallShapeAsync(new()
        {
            ["items"] = new[] { new { name = awkward } },
        });

        // Survives encoding, the script's ConvertFrom-Json, and the script's own output.
        Assert.Equal(awkward, report.GetProperty("itemsFirstName").GetString());

        // And survives as the characters themselves. The default encoder would rewrite each of them
        // as a six-character \u escape - still valid JSON parsing back to the same value, which is
        // why the assertion above cannot tell the two encoders apart. The absence of escapes in the
        // text the script received is what distinguishes them.
        var raw = report.GetProperty("itemsRaw").GetString()!;
        Assert.Equal("""[{"name":"café — 日本語 <tag> & 'quoted' a+b"}]""", raw);
        Assert.DoesNotContain("\\u", raw, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-admin-token";
        public const string ReadOnlyToken = "test-readonly-token";

        public string ToolsDir { get; }

        /// <summary>The directory the <c>mark</c> fixture tool writes its marker files to. Their
        /// presence is how a test tells whether a script actually ran.</summary>
        public string MarkerDir { get; }

        public Fixture() : base("vault-mcp-tools-dispatch-tests-")
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
                [AdminToken] = new { id = "admin", opClasses = new[] { "content:read", "content:write" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            ToolsDir = toolsDir;
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

            // The argument-encoding fixture tool. Every parameter is [string], as the real tools are
            // so they can still be run standalone with `pwsh -File`, and the .SCHEMA side-cars are
            // the only difference between them. A test that varies only which parameter it sends a
            // structured value to therefore isolates the encoding signal.
            File.WriteAllText(Path.Combine(toolsDir, "shape.ps1"), """
                <#
                .SYNOPSIS
                Reports how each of its arguments looked by the time it reached the script.
                .PARAMETER items
                A side-car-array parameter that stays [string] in the param() block and parses its
                own JSON.
                .SCHEMA items
                {
                  "type": "array",
                  "items": { "type": "object" },
                  "description": "An ordered array of objects, each with a \"name\"."
                }
                .PARAMETER payload
                A side-car-object parameter, also [string].
                .SCHEMA payload
                { "type": "object", "description": "An object carrying a \"message\"." }
                .PARAMETER declared_string
                A parameter whose side-car declares type "string" - present, but declaring the type
                the .NET parameter already has, so it is not an encoding parameter.
                .SCHEMA declared_string
                { "type": "string", "description": "Plain text." }
                .PARAMETER plain
                A parameter with no side-car at all.
                .PARAMETER count
                The one parameter here that is not [string]. MapJsonType derives [long] as JSON
                "integer", so this is what a tool declaring a numeric parameter would look like -
                and the only way to see what the splat actually delivers for a JSON number.
                .NOTES
                OpClass: content:write
                #>
                [CmdletBinding()]
                param(
                    [string] $items,
                    [string] $payload,
                    [string] $declared_string,
                    [string] $plain,
                    [long] $count
                )
                $report = [ordered]@{ itemsRaw = $items; declaredString = $declared_string; plain = $plain }
                if ($PSBoundParameters.ContainsKey('count')) {
                    # Reported as text: the point is which CLR type the splat delivered and whether
                    # the value survived intact, and a JSON number here would re-round-trip both.
                    # Read off PSBoundParameters rather than off $count, which the binder has
                    # already coerced to [long] and which would therefore report [long] either way.
                    $report.countType = $PSBoundParameters['count'].GetType().Name
                    $report.countValue = $count.ToString([System.Globalization.CultureInfo]::InvariantCulture)
                }
                if (-not [string]::IsNullOrWhiteSpace($items)) {
                    # The same parse the real tools do, -NoEnumerate and all: this is what has to
                    # succeed for the encoding to be worth anything.
                    $parsed = ConvertFrom-Json -InputObject $items -NoEnumerate
                    $report.itemsIsArray = $parsed -is [System.Object[]]
                    $report.itemsCount = @($parsed).Count
                    $report.itemsFirstName = @($parsed)[0].name
                }
                if (-not [string]::IsNullOrWhiteSpace($payload)) {
                    $report.payloadMessage = (ConvertFrom-Json -InputObject $payload).message
                }
                $report | ConvertTo-Json -Depth 6 -Compress
                """);

            MarkerDir = Path.Combine(TempDir, "markers");
            Directory.CreateDirectory(MarkerDir);
            // Carries [CmdletBinding()] like the real tool scripts, so a call reaching the runspace
            // with an unadvertised name would be refused by the binder, which is what the
            // dispatcher's own check has to get in front of.
            File.WriteAllText(Path.Combine(toolsDir, "mark.ps1"), """
                <#
                .SYNOPSIS
                Writes a marker file, so a test can tell whether the script actually ran.
                .PARAMETER Marker
                Full path of the marker file to write.
                .NOTES
                OpClass: content:write
                #>
                [CmdletBinding()]
                param([Parameter(Mandatory=$true)] [string]$Marker)
                Set-Content -LiteralPath $Marker -Value 'ran'
                Write-Output "marked"
                """);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
