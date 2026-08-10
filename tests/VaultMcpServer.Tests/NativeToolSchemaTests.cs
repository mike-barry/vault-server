using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Tests;
using VaultShared.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Holds every declared tool's <c>inputSchema</c>, pinned verbatim. Each schema is derived from the
/// tool's argument record; the derivation may change how a schema is produced but not what it says,
/// because this text is the wire contract every caller reads.
/// </summary>
/// <remarks>
/// These literals are shared with the dispatch-level tests that assert the same text arrives over
/// HTTP, so each one is checked both against the tool object and against a real <c>tools/list</c>
/// response and neither can drift on its own.
/// </remarks>
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
          "required": [ "scope_roots", "op_class" ],
          "additionalProperties": false
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
          },
          "additionalProperties": false
        }
        """;

    public const string HealthCheck = """
        { "type": "object", "properties": {}, "additionalProperties": false }
        """;

    // The vault tools follow. The argument record is the only statement of the surface, and a
    // member renamed, reordered, made required or dropped from an enumeration is a change every
    // caller sees.

    public const string Read = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Vault-relative path of the note, carrying the \".md\" extension. The extension is never inferred."
            },
            "vault": {
              "type": "string",
              "description": "The vault to read in. Optional; the configured default vault is used when it is absent."
            },
            "part": {
              "type": "string",
              "enum": [ "all", "frontmatter", "body" ],
              "default": "all",
              "description": "Which facet to return: the whole note, the frontmatter block's inner text without its fences, or the body. Defaults to the whole note."
            },
            "with_content_hash": {
              "type": "boolean",
              "default": false,
              "description": "Whether to return the whole file's content hash alongside the content, for a later write to guard against a change it never saw. Defaults to false."
            }
          },
          "required": [ "path" ],
          "additionalProperties": false
        }
        """;

    public const string Glob = """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "Glob pattern, matched case-insensitively, with \"**\" for recursive descent. A pattern containing no separator matches a path's final segment at any depth; one containing a separator is anchored at the vault root."
            },
            "vault": {
              "type": "string",
              "description": "The vault to search in. Optional; the configured default vault is used when it is absent."
            }
          },
          "required": [ "pattern" ],
          "additionalProperties": false
        }
        """;

    public const string Grep = """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "Regular expression, in the search engine's syntax, matched against note content. Matching is case-sensitive; prefix the pattern with (?i) to match without regard to case."
            },
            "vault": {
              "type": "string",
              "description": "The vault to search in. Optional; the configured default vault is used when it is absent."
            },
            "granularity": {
              "type": "string",
              "enum": [ "lines", "files", "counts" ],
              "default": "lines",
              "description": "How much of the answer to return: every matching line, the notes containing a match, or a match count per note. Defaults to lines."
            },
            "path_glob": {
              "type": "string",
              "description": "Optional glob narrowing which notes are searched, matched the same way glob's pattern is."
            }
          },
          "required": [ "pattern" ],
          "additionalProperties": false
        }
        """;

    public const string Edit = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Vault-relative path of the note, carrying the \".md\" extension. The extension is never inferred."
            },
            "vault": {
              "type": "string",
              "description": "The vault the note is in. Optional; the configured default vault is used when it is absent."
            },
            "operations": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "op": {
                    "type": "string",
                    "description": "The operation: \"set\", \"remove\", \"add_entry\" or \"remove_entry\". Inside an anchor, \"before\" or \"after\"."
                  },
                  "key": {
                    "type": "string",
                    "description": "The frontmatter key being mutated. Omitted inside an anchor, which addresses an entry under its operation's own key."
                  },
                  "value": {
                    "type": "string",
                    "description": "The new value. \"set\" only."
                  },
                  "entry": {
                    "description": "The entry to add: an object of the variant's fields, or a bare string for a scalar variant. \"add_entry\" only."
                  },
                  "target": {
                    "type": "string",
                    "description": "The identity of the entry to remove, or - inside an anchor - of the entry the new one lands beside."
                  },
                  "variant": {
                    "type": "string",
                    "description": "A variant to clear wholesale, with no target named. \"remove_entry\" only, and only where the declaration permits it."
                  },
                  "anchor": {
                    "type": "object",
                    "properties": {
                      "op": {
                        "type": "string",
                        "description": "The operation: \"set\", \"remove\", \"add_entry\" or \"remove_entry\". Inside an anchor, \"before\" or \"after\"."
                      },
                      "key": {
                        "type": "string",
                        "description": "The frontmatter key being mutated. Omitted inside an anchor, which addresses an entry under its operation's own key."
                      },
                      "value": {
                        "type": "string",
                        "description": "The new value. \"set\" only."
                      },
                      "entry": {
                        "description": "The entry to add: an object of the variant's fields, or a bare string for a scalar variant. \"add_entry\" only."
                      },
                      "target": {
                        "type": "string",
                        "description": "The identity of the entry to remove, or - inside an anchor - of the entry the new one lands beside."
                      },
                      "variant": {
                        "type": "string",
                        "description": "A variant to clear wholesale, with no target named. \"remove_entry\" only, and only where the declaration permits it."
                      },
                      "anchor": {
                        "$ref": "#/properties/operations/items/properties/anchor",
                        "description": "Where an added entry goes: an object carrying \"op\" of \"before\" or \"after\" and \"target\" naming an entry already in the list. Absent appends."
                      }
                    },
                    "required": [
                      "op"
                    ],
                    "additionalProperties": false,
                    "description": "Where an added entry goes: an object carrying \"op\" of \"before\" or \"after\" and \"target\" naming an entry already in the list. Absent appends."
                  }
                },
                "required": [
                  "op"
                ],
                "additionalProperties": false
              },
              "description": "Frontmatter operations, applied in order, each seeing the previous one's result. Repetition is meaningful."
            },
            "body": {
              "type": "object",
              "properties": {
                "content": {
                  "type": "string",
                  "description": "The whole new body, replacing everything after the frontmatter block. Requires \"expected_content_hash\"."
                },
                "find": {
                  "type": "string",
                  "description": "Text to substitute, which must occur exactly once in the body. Zero matches and more than one are each refused."
                },
                "replace": {
                  "type": "string",
                  "description": "What replaces the matched text. Empty deletes it. Sent with \"find\"."
                }
              },
              "additionalProperties": false,
              "description": "A change to the note's body: either a wholesale replacement, or a substitution that must match exactly once."
            },
            "expected_content_hash": {
              "type": "string",
              "description": "The content hash a prior read returned. Required for a wholesale body replacement, optional on any other edit, and the edit is refused if the file has changed since."
            }
          },
          "required": [
            "path"
          ],
          "additionalProperties": false
        }
        """;

    public const string Create = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Vault-relative path to bring into existence. A note carries the \".md\" extension; a folder carries none. The extension is never inferred."
            },
            "kind": {
              "type": "string",
              "enum": [ "note", "folder" ],
              "description": "Which of the two the path names: \"note\" or \"folder\". Required, because the two take different arguments."
            },
            "vault": {
              "type": "string",
              "description": "The vault to create in. Optional; the configured default vault is used when it is absent."
            },
            "content": {
              "type": "string",
              "description": "The note's finished text, frontmatter block included where it has one. Notes only, and required for one: nothing is templated or composed here, and empty text creates an empty note."
            },
            "git_keep": {
              "type": "boolean",
              "default": false,
              "description": "Whether to seed the folder's \".gitkeep\", so a folder expected to exist while empty survives a repository round trip. Folders only; off by default."
            }
          },
          "required": [ "path", "kind" ],
          "additionalProperties": false
        }
        """;

    public const string Move = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Vault-relative path of the note or folder to relocate. A note carries the \".md\" extension; a folder carries none."
            },
            "destination": {
              "type": "string",
              "description": "Vault-relative path to move it to, of the same kind. Its parent must already exist - nothing brings a folder into existence on the way past - and it must be unoccupied."
            },
            "vault": {
              "type": "string",
              "description": "The vault to move within. Optional; the configured default vault is used when it is absent."
            },
            "kind": {
              "type": "string",
              "enum": [ "note", "folder" ],
              "description": "Assert which kind the path names: \"note\" or \"folder\". Optional, and a fail-fast only - both kinds take the same arguments here."
            },
            "apply": {
              "type": "boolean",
              "default": false,
              "description": "Apply the move. Absent or false answers the same result with \"applied\" false: where everything lands, and every wikilink the move would repair or pin."
            }
          },
          "required": [ "path", "destination" ],
          "additionalProperties": false
        }
        """;

    public const string Delete = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Vault-relative path to remove. A note carries the \".md\" extension; a folder carries none. The extension is never inferred."
            },
            "kind": {
              "type": "string",
              "enum": [ "note", "folder" ],
              "description": "Which of the two the path names: \"note\" or \"folder\". Required, because the two take different arguments."
            },
            "vault": {
              "type": "string",
              "description": "The vault to remove from. Optional; the configured default vault is used when it is absent."
            },
            "recursive": {
              "type": "boolean",
              "default": false,
              "description": "Remove a folder together with everything under it. Folders only. Without it, a folder that holds anything is refused and the refusal carries the full set the removal would destroy."
            },
            "apply": {
              "type": "boolean",
              "default": false,
              "description": "Apply the removal. Absent or false answers the same result with \"applied\" false: the full set that would be destroyed, and every surviving wikilink into it."
            }
          },
          "required": [ "path", "kind" ],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// Re-serializes a schema compactly, so whitespace and <c>\u</c> escaping differences between a
    /// literal here and a serializer's output do not read as a schema difference.
    /// </summary>
    /// <param name="json">The schema text to normalize.</param>
    /// <returns>The same schema, compactly serialized.</returns>
    /// <remarks>
    /// Member order is not normalized. The derived schema follows constructor-parameter order, and
    /// reordering members changes what a model reads first.
    /// </remarks>
    public static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    /// <summary>Re-serializes a parsed schema compactly, matching the string overload.</summary>
    /// <param name="element">The parsed schema to normalize.</param>
    /// <returns>The same schema, compactly serialized.</returns>
    public static string Canonical(JsonElement element) => JsonSerializer.Serialize(element);
}

/// <summary>
/// Pins what each declared tool advertises. <see cref="NativeToolArguments"/> derives the schema
/// from the argument record the declaration names rather than from a hand-written
/// <c>Dictionary&lt;string, object&gt;</c>, and the derivation may change how the schema is produced
/// but not what it says.
/// </summary>
/// <remarks>
/// Argument binding happens outside the tool. A binding failure is not a
/// <c>{code, message, data?}</c> error until the shared execute path re-raises it as one, and that
/// is the only place it happens, so the binding tests below drive the execute path rather than a
/// tool.
/// </remarks>
public sealed class NativeToolSchemaTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
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

    private static JsonElement SchemaOf(string toolName) => NativeToolArguments.SchemaFor(toolName switch
    {
        "mint_token" => MintTokenTool.Declaration.Arguments,
        "request_elevation" => ElevationRequestTool.Declaration.Arguments,
        "read" => ReadTool.Declaration.Arguments,
        "glob" => GlobTool.Declaration.Arguments,
        "grep" => GrepTool.Declaration.Arguments,
        "edit" => EditTool.Declaration.Arguments,
        "create" => CreateTool.Declaration.Arguments,
        "move" => MoveTool.Declaration.Arguments,
        "delete" => DeleteTool.Declaration.Arguments,
        _ => HealthCheckTool.Declaration.Arguments,
    });

    /// <summary>
    /// Verifies that the derived schema matches the pinned literal exactly. Property names, order,
    /// types, <c>items</c>, descriptions and the <c>required</c> set all have to match, because
    /// every one of them is on the wire.
    /// </summary>
    /// <param name="toolName">The tool whose schema is derived.</param>
    /// <param name="expected">The pinned schema text for that tool.</param>
    [Theory]
    [MemberData(nameof(Pinned))]
    public void InputSchema_MatchesTheHandAuthoredContractItReplaced(string toolName, string expected)
    {
        Assert.Equal(NativeToolSchemas.Canonical(expected), NativeToolSchemas.Canonical(SchemaOf(toolName)));
    }

    /// <summary>
    /// Verifies that arguments are advertised under their snake_case wire names.
    /// </summary>
    /// <param name="toolName">The tool whose schema is derived.</param>
    /// <param name="expected">The argument names the schema must advertise, in order.</param>
    /// <remarks>
    /// The exporter and the binder share one <see cref="JsonSerializerOptions"/>. Dropping
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/> from it renames every multi-word argument to
    /// camelCase on both sides at once, leaving schema and binding agreeing with each other while
    /// breaking every existing caller. Nothing else in the suite would catch that.
    /// </remarks>
    [Theory]
    [InlineData("mint_token", "scope_roots", "op_class", "ttl")]
    [InlineData("request_elevation", "grant", "scope_roots", "op_class", "ttl", "session_context")]
    [InlineData("read", "path", "vault", "part", "with_content_hash")]
    [InlineData("grep", "pattern", "vault", "granularity", "path_glob")]
    public void InputSchema_AdvertisesTheSnakeCaseWireNames(string toolName, params string[] expected)
    {
        var advertised = SchemaOf(toolName).GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(expected, advertised);
    }

    /// <summary>
    /// Verifies that C# nullability does not reach the wire. The exporter emits
    /// <c>["string","null"]</c> unions for optional members with a <c>"default": null</c> beside
    /// them, and the shared <c>TransformSchemaNode</c> removes both.
    /// </summary>
    /// <param name="toolName">The tool whose schema is derived.</param>
    [Theory]
    [InlineData("mint_token")]
    [InlineData("request_elevation")]
    // glob rather than read or grep. Those two declare real non-null defaults, which the pinned
    // literals above already state exactly.
    [InlineData("glob")]
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
    /// Verifies that an argument of the wrong type is refused as <c>invalid_argument</c> naming that
    /// argument.
    /// </summary>
    /// <remarks>
    /// <c>JsonSerializer.Deserialize</c> raises <see cref="JsonException"/>, which is not the
    /// <c>{code, message, data?}</c> shape every other refusal carries. The execute path re-raises
    /// it in one place rather than each tool doing so, and a caller branching on <c>code</c> handles
    /// it exactly like a semantic refusal instead of receiving a bare serializer message.
    /// </remarks>
    [Fact]
    public void MintToken_ArgumentOfTheWrongType_IsReRaisedAsInvalidArgumentNamingIt()
    {
        // scope_roots is advertised as an array; a bare string never reaches the tool's own checks.
        var refusal = Refuse(MakeStore(), new { scope_roots = "test", op_class = new[] { "content:read" } });

        Assert.Equal(("invalid_argument", "scope_roots"), Refusal(refusal));
    }

    /// <summary>
    /// Verifies that a wrongly typed array element is attributed to the argument rather than to
    /// <c>$</c>. The binder reports the path <c>$.op_class[0]</c>, and the index is trimmed off.
    /// </summary>
    [Fact]
    public void MintToken_ArrayElementOfTheWrongType_IsAttributedToTheArgumentNotTheIndex()
    {
        var refusal = Refuse(MakeStore(), new { scope_roots = new[] { "test" }, op_class = new object[] { 7 } });

        Assert.Equal(("invalid_argument", "op_class"), Refusal(refusal));
    }

    /// <summary>
    /// Verifies that an argument name the record does not declare is refused naming that name, on a
    /// tool of its own rather than only on <c>edit</c>. The binder is shared by every declared tool,
    /// so a member it dropped would be dropped everywhere.
    /// </summary>
    [Fact]
    public void MintToken_UndeclaredArgument_IsRefusedNamingIt()
    {
        var refusal = Refuse(MakeStore(), new { scope_roots = new[] { "test" }, op_class = new[] { "content:read" }, tll = 30 });

        Assert.Equal(("invalid_argument", "tll"), Refusal(refusal));
        Assert.Contains("takes no argument named \"tll\"", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the advertised schema states the same rule the binder applies, rather than
    /// advertising a surface that accepts anything while the binder refuses an unknown member.
    /// </summary>
    [Fact]
    public void InputSchema_AdvertisesThatNoOtherMemberIsAccepted()
    {
        foreach (var row in Pinned())
        {
            var toolName = (string)row[0]!;
            Assert.False(SchemaOf(toolName).GetProperty("additionalProperties").GetBoolean(), toolName);
        }
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
