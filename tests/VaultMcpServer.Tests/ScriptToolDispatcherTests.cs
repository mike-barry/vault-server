using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end: boots the real host against a temp tools/ folder and drives tools/list and
/// tools/call over HTTP, proving script discovery, schema exposure, execution, and
/// per-tool op-class enforcement against the caller's grant all work together - not just
/// each piece in isolation.
/// </summary>
[Collection("Program host tests")]
public sealed class ScriptToolDispatcherTests : IClassFixture<ScriptToolDispatcherTests.Fixture>
{
    private readonly Fixture _fixture;

    public ScriptToolDispatcherTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>A `tools/call` request body, serialized rather than spelled as a literal so an
    /// argument name or value supplied by the test (a file path, a theory case) is escaped rather
    /// than pasted into JSON.</summary>
    private static string ToolCallBody(string toolName, Dictionary<string, string> arguments) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = toolName, arguments },
        });

    /// <summary>The same body, with arguments that are not all strings - a real JSON array or
    /// object for a structured argument, so the encoding tests drive the wire shape a client
    /// actually sends rather than a pre-stringified stand-in.</summary>
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
        // Streamable HTTP requires both media types accepted (406 otherwise); the server
        // replies as an SSE stream when text/event-stream is present, so responses need
        // ExtractJsonRpcPayload below rather than a direct JsonDocument.Parse.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    /// <summary>Extracts the JSON-RPC payload from a response body that may be a single SSE `data:` event rather than a plain JSON object.</summary>
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
        Assert.True(greet.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("Name", out _));
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

        // The floor denial carries the shared {code, message, data?} shape - the same code the
        // in-script key assertion (Assert-VaultOpClass) throws, so one branch covers both layers.
        var errorText = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        Assert.Equal("insufficient_capability", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("content:write", error.RootElement.GetProperty("data").GetProperty("requiredOpClass").GetString());
        Assert.Equal("greet", error.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }

    /// <summary>
    /// The denial names the class the tool required - actionable for an elevation request - and
    /// nothing about the classes the caller's grant actually holds, so a denial never doubles as a
    /// capability dump.
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
    /// End-to-end proof that the `tools/` file watcher is wired into the real host: a
    /// script dropped onto disk after startup shows up in `tools/list` without a restart,
    /// once the watcher's debounce window has elapsed.
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

        // An unknown name is refused before any script runs, so it emits the shared shape too -
        // `not_found` over the caller's own tool name, which discloses nothing about the tool set.
        var errorText = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var error = JsonDocument.Parse(errorText);
        Assert.Equal("not_found", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("no_such_tool", error.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }

    /// <summary>
    /// The dispatcher's argument-name check matches case-insensitively because that is how
    /// PowerShell's binder matches - a name that would have bound must not be refused here, or the
    /// check becomes stricter than the thing it stands in front of.
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
    /// An argument naming no advertised parameter is refused by the dispatcher as a coded
    /// <c>invalid_argument</c>, rather than reaching the runspace where <c>[CmdletBinding()]</c>
    /// would refuse it as a raw PowerShell binding string outside the shared error shape.
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
    /// <c>[CmdletBinding()]</c> makes PowerShell's common parameters bindable on every tool script.
    /// They are not special-cased: they name no entry in the param() block, so the same check
    /// refuses them, and a tool's param() block stays its entire client-facing argument surface.
    /// </summary>
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
    /// The refusal happens before the runner is invoked, not after the script has already had its
    /// effect: the same call minus the bogus argument writes the marker file, so the absence of the
    /// file on the refused call is proof the script never ran.
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
        // assertion above is about the check and not about the fixture script being inert.
        using var allowed = await client.PostAsync("/mcp", JsonBody(ToolCallBody("mark", new() { ["Marker"] = allowedMarker })));
        using var allowedDoc = JsonDocument.Parse(ExtractJsonRpcPayload(await allowed.Content.ReadAsStringAsync()));
        var allowedResult = allowedDoc.RootElement.GetProperty("result");
        Assert.False(allowedResult.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.True(File.Exists(allowedMarker));
    }

    /// <summary>Runs a `shape` call and returns its parsed report - the tool's real output, so
    /// every encoding assertion below is about what the script actually parsed rather than about
    /// what the dispatcher put in the splat.</summary>
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
    /// A real JSON array sent for a parameter whose side-car declares <c>type: "array"</c> reaches
    /// the <c>[string]</c> script parameter as JSON text, which the script's own
    /// <c>ConvertFrom-Json -NoEnumerate</c> parses back into the array it was. Without the encoding
    /// the splat hands PowerShell an <c>object[]</c>, which stringifies into a <c>[string]</c>
    /// parameter as "System.Object[]" and fails to parse at all.
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
    /// The rule that makes the encoding safe to switch on: only a <em>structured</em> arrival value
    /// is serialized. A caller that already encoded its payload - every caller written against the
    /// contract as it stands today, and every hand-encoded remedy splat - sends a JSON string, and
    /// it must pass through verbatim. Encoding it a second time would hand the script a JSON string
    /// literal where it expects an array, which parses as a bare string and is refused.
    /// </summary>
    [Fact]
    public async Task ToolsCall_AlreadyEncodedStringForSideCarArrayParameter_IsNotEncodedAgain()
    {
        const string encoded = """[{"name":"first"},{"name":"second"}]""";
        var report = await CallShapeAsync(new() { ["items"] = encoded });

        // Verbatim: the same characters the caller sent, not a JSON string literal wrapping them.
        Assert.Equal(encoded, report.GetProperty("itemsRaw").GetString());
        Assert.True(report.GetProperty("itemsIsArray").GetBoolean());
        Assert.Equal(2, report.GetProperty("itemsCount").GetInt32());
        Assert.Equal("first", report.GetProperty("itemsFirstName").GetString());
    }

    /// <summary>The structured-array and already-encoded-string arrivals converge on byte-identical
    /// script input - the property that lets a caller send either form.</summary>
    [Fact]
    public async Task ToolsCall_StructuredAndPreEncodedArrivals_ReachTheScriptIdentically()
    {
        var structured = await CallShapeAsync(new() { ["items"] = new[] { new { name = "first" } } });
        var preEncoded = await CallShapeAsync(new() { ["items"] = """[{"name":"first"}]""" });

        Assert.Equal(structured.GetProperty("itemsRaw").GetString(), preEncoded.GetProperty("itemsRaw").GetString());
    }

    /// <summary>The signal is a non-string declared type, not "array" specifically: an object-typed
    /// side-car encodes a structured object the same way.</summary>
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
    /// The two non-encoding cases, together so the contrast is in one place: a parameter with no
    /// side-car at all, and one whose side-car declares <c>type: "string"</c>. Both take exactly
    /// today's path - a string argument arrives as itself.
    /// </summary>
    [Fact]
    public async Task ToolsCall_ParametersWithNoSideCarOrAStringSideCar_PassStringsThroughUnchanged()
    {
        var report = await CallShapeAsync(new()
        {
            ["plain"] = """[{"name":"first"}]""",
            ["declared_string"] = "just text",
        });

        // A JSON-looking string against a parameter with no side-car is still just a string: the
        // dispatcher has no schema to read, so it does nothing to it in either direction.
        Assert.Equal("""[{"name":"first"}]""", report.GetProperty("plain").GetString());
        Assert.Equal("just text", report.GetProperty("declaredString").GetString());
    }

    /// <summary>
    /// Round-trip fidelity through the encoder's chosen options. The payload carries non-ASCII and
    /// the characters <c>System.Text.Json</c>'s <em>default</em> encoder escapes for HTML safety
    /// (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>+</c>, <c>'</c>) - a vault path or a frontmatter
    /// value can hold every one of them. The relaxed encoder writes them as themselves, so the
    /// script sees the bytes the caller sent; the default encoder would still produce valid JSON
    /// that parses back to the same value, but the payload text - which a tool echoes into its own
    /// error data and its logs - would be an unreadable escape run.
    /// </summary>
    [Fact]
    public async Task ToolsCall_PayloadWithNonAsciiAndDefaultEscapeSetCharacters_RoundTripsVerbatim()
    {
        const string awkward = "café — 日本語 <tag> & 'quoted' a+b";
        var report = await CallShapeAsync(new()
        {
            ["items"] = new[] { new { name = awkward } },
        });

        // Survives encode -> ConvertFrom-Json -> the script's own output.
        Assert.Equal(awkward, report.GetProperty("itemsFirstName").GetString());

        // And survives as the characters themselves. System.Text.Json's default encoder would have
        // rewritten every one of them as a six-character backslash-u escape - still valid JSON,
        // still parsing back to the same value, which is why the assertion above cannot tell the
        // two encoders apart. The absence of any such escape run in the text the script actually
        // received is what pins the choice.
        var raw = report.GetProperty("itemsRaw").GetString()!;
        Assert.Equal("""[{"name":"café — 日本語 <tag> & 'quoted' a+b"}]""", raw);
        Assert.DoesNotContain("\\u", raw, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-admin-token";
        public const string ReadOnlyToken = "test-readonly-token";

        public string ToolsDir { get; }

        /// <summary>Where the <c>mark</c> fixture tool writes its marker files - the observable side
        /// effect a test uses to tell whether a script actually ran.</summary>
        public string MarkerDir { get; }

        public Fixture() : base("vault-mcp-tools-dispatch-tests-")
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
                [AdminToken] = new { id = "admin", opClasses = new[] { "content:read", "content:write" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>() } },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>() } },
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

            // The argument-encoding fixture. Every parameter is [string] - exactly like the real
            // tools, whose standalone `pwsh -File` invariant is what keeps them that way - and the
            // `.SCHEMA` side-cars are the only thing that differs between them, so a test that
            // varies only which parameter it sends a structured value to isolates the signal.
            File.WriteAllText(Path.Combine(toolsDir, "shape.ps1"), """
                <#
                .SYNOPSIS
                Reports how each of its arguments looked by the time it reached the script.
                .PARAMETER items
                A side-car-array parameter that stays [string] in the param() block and parses its
                own JSON, the way edit's frontmatter_ops does.
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
                .NOTES
                OpClass: content:write
                #>
                [CmdletBinding()]
                param(
                    [string] $items,
                    [string] $payload,
                    [string] $declared_string,
                    [string] $plain
                )
                $report = [ordered]@{ itemsRaw = $items; declaredString = $declared_string; plain = $plain }
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
            // Carries [CmdletBinding()] like the real tool scripts, so a call that reaches the
            // runspace with an unadvertised name would be refused by the binder - which is exactly
            // what the dispatcher's own check has to get in front of.
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
