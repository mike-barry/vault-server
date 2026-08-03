using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Tests;
using VaultShared.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// The three native tools' <c>inputSchema</c> literals, exactly as they were hand-authored before
/// the schema derived from the argument record. They are the acceptance criterion for the
/// conversion: the derivation is allowed to change how the schema is <em>produced</em> and nothing
/// about what it <em>says</em>, because every one of these strings is already on the wire.
///
/// Shared with the dispatch-level test that asserts the same text arrives over HTTP - so the
/// pinned form is checked against the tool object here and against the real <c>tools/list</c>
/// response there, and neither can drift alone.
/// </summary>
public static class NativeToolSchemas
{
    public const string MintToken = """
        {
          "type": "object",
          "properties": {
            "scope_roots": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Folder-prefix scope roots for the minted grant, e.g. \"primary\" (a whole vault) or \"primary/Projects/Foo\" (a subtree). At least one required; \"*\" is not accepted."
            },
            "op_class": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Explicit op classes the minted grant allows, e.g. [\"content:read\"]. Required, at least one; \"*\" is not accepted."
            },
            "ttl": {
              "type": "integer",
              "description": "Minutes until the minted grant expires. Optional, defaults to 60. \"*\" (infinite) is not accepted."
            }
          },
          "required": [ "scope_roots", "op_class" ]
        }
        """;

    public const string RequestElevation = """
        {
          "type": "object",
          "properties": {
            "grant": {
              "type": "string",
              "description": "Id of a named grant to elevate to (e.g. \"admin\"). Provide this OR scope_roots+op_class, not both."
            },
            "scope_roots": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Folder-prefix scope roots for a freshly-minted scoped target, e.g. \"primary/Projects/Foo\". Provide with op_class instead of grant."
            },
            "op_class": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Explicit op classes for a freshly-minted scoped target, e.g. [\"Read\"]. Provide with scope_roots."
            },
            "ttl": {
              "type": "integer",
              "description": "Minutes the elevation lasts before reverting to the base binding. Optional; defaults to the server's configured elevation TTL."
            },
            "session_context": {
              "type": "string",
              "description": "Optional free-text context about why elevation is needed, shown to a human approver."
            }
          }
        }
        """;

    public const string HealthCheck = """
        { "type": "object", "properties": {} }
        """;

    /// <summary>Compact re-serialization, so whitespace and <c>\u</c>-escaping differences between a
    /// literal here and a serializer's output do not read as a schema difference. Member order is
    /// deliberately <em>not</em> normalized - the derived schema follows constructor-parameter order,
    /// and a reordering is a real change to what a model reads first.</summary>
    public static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    public static string Canonical(JsonElement element) => JsonSerializer.Serialize(element);
}

/// <summary>
/// Pins what the three declared tools advertise: <see cref="NativeToolArguments"/> derives it from
/// the argument record the declaration names, rather than from a hand-authored
/// <c>Dictionary&lt;string, object&gt;</c> literal, and the derivation may change how the schema is
/// <em>produced</em> and nothing about what it <em>says</em>.
///
/// The binding boundary moved with the conversion: a binding failure is not a
/// <c>{code, message, data?}</c> error until it is re-raised as one, and the shared execute path is
/// now the single site that does that, so the cases below drive it rather than a tool.
/// </summary>
public sealed class NativeToolSchemaTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    public static TheoryData<string, string> Pinned() => new()
    {
        { "mint_token", NativeToolSchemas.MintToken },
        { "request_elevation", NativeToolSchemas.RequestElevation },
        { "health_check", NativeToolSchemas.HealthCheck },
    };

    private static JsonElement SchemaOf(string toolName) => NativeToolArguments.SchemaFor(toolName switch
    {
        "mint_token" => MintTokenTool.Declaration.Arguments,
        "request_elevation" => ElevationRequestTool.Declaration.Arguments,
        _ => HealthCheckTool.Declaration.Arguments,
    });

    /// <summary>
    /// The whole conversion in one assertion: the derived schema is byte-for-byte what the
    /// hand-authored literal said. Property names, order, types, <c>items</c>, descriptions, and the
    /// <c>required</c> set all have to survive, since every one of them is already on the wire.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pinned))]
    public void InputSchema_MatchesTheHandAuthoredContractItReplaced(string toolName, string expected)
    {
        Assert.Equal(NativeToolSchemas.Canonical(expected), NativeToolSchemas.Canonical(SchemaOf(toolName)));
    }

    /// <summary>
    /// The naming-policy guard, stated on its own so it fails legibly. The exporter and the binder
    /// share one <see cref="JsonSerializerOptions"/>; dropping
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/> from it renames every multi-word argument to
    /// camelCase on both sides at once, which leaves schema and binding agreeing with each other
    /// while breaking every existing caller. Nothing else in the suite would notice.
    /// </summary>
    [Theory]
    [InlineData("mint_token", "scope_roots", "op_class", "ttl")]
    [InlineData("request_elevation", "grant", "scope_roots", "op_class", "ttl", "session_context")]
    public void InputSchema_AdvertisesTheSnakeCaseWireNames(string toolName, params string[] expected)
    {
        var advertised = SchemaOf(toolName).GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(expected, advertised);
    }

    /// <summary>
    /// C# nullability must not leak onto the wire. The exporter emits <c>["string","null"]</c>-style
    /// unions for every optional member and a <c>"default": null</c> beside them; the shared
    /// <c>TransformSchemaNode</c> collapses both, and an added argument gets that treatment for free.
    /// </summary>
    [Theory]
    [InlineData("mint_token")]
    [InlineData("request_elevation")]
    public void InputSchema_CarriesNoNullUnionsOrNullDefaults(string toolName)
    {
        var text = NativeToolSchemas.Canonical(SchemaOf(toolName));
        Assert.DoesNotContain("\"null\"", text);
        Assert.DoesNotContain("\"default\"", text);
    }

    // --- The binding boundary -------------------------------------------------------------

    private static Dictionary<string, JsonElement> Args(object obj) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(obj))!;

    private static (string Code, string? Argument) Refusal(ToolRefusal refusal)
    {
        using var doc = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        return (
            doc.RootElement.GetProperty("code").GetString()!,
            doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("argument", out var argument)
                ? argument.GetString()
                : null);
    }

    private ToolRefusal Refuse(GrantStore store, object arguments)
    {
        var registry = new ToolRegistry().Add(MintTokenTool.Declaration, new MintTokenTool(store, OpClasses, fixture.Config));
        var execution = new ToolExecution(fixture.Config);
        return Assert.Throws<ToolRefusal>(() => execution.Invoke(
            registry.Find("mint_token")!.Value, Args(arguments), store.Authenticate("admin-token")!, new ClientSession("principal")));
    }

    /// <summary>
    /// <c>JsonSerializer.Deserialize</c> raises <see cref="JsonException"/>, which is not the
    /// <c>{code, message, data?}</c> shape every other refusal on this server carries. The execute
    /// path re-raises it as <c>invalid_argument</c> naming the argument - one site rather than one
    /// per tool - so a caller that sends the wrong type branches on <c>code</c> exactly as it does
    /// for a semantic refusal, rather than getting a bare serializer message.
    /// </summary>
    [Fact]
    public void MintToken_ArgumentOfTheWrongType_IsReRaisedAsInvalidArgumentNamingIt()
    {
        // scope_roots is advertised as an array; a bare string never reaches the tool's own checks.
        var refusal = Refuse(MakeStore(), new { scope_roots = "test", op_class = new[] { "content:read" } });

        Assert.Equal(("invalid_argument", "scope_roots"), Refusal(refusal));
    }

    /// <summary>An element of the wrong type is attributed to the argument, not to <c>$</c>: the
    /// binder reports the path <c>$.op_class[0]</c> and the index is trimmed off.</summary>
    [Fact]
    public void MintToken_ArrayElementOfTheWrongType_IsAttributedToTheArgumentNotTheIndex()
    {
        var refusal = Refuse(MakeStore(), new { scope_roots = new[] { "test" }, op_class = new object[] { 7 } });

        Assert.Equal(("invalid_argument", "op_class"), Refusal(refusal));
    }

    private static readonly OpClassRegistry OpClasses =
        OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private GrantStore MakeStore()
    {
        var dir = Directory.CreateTempSubdirectory("vault-mcp-native-schema-tests-").FullName;
        var path = Path.Combine(dir, "grants.json");
        File.WriteAllText(path, """
            { "admin-token": { "id": "admin", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }
            """);
        return GrantStore.Load(path, OpClasses, fixture.Config, new ServerHealthState());
    }
}
