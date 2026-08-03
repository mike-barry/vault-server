using System.Text.Json;
using VaultShared.Auth;
using VaultShared.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Drives one declared tool's own body, without the shared execute path in front of it. Arguments
/// are bound through the declaration exactly as the execute path binds them, and a refusal is caught
/// rather than propagated, so a unit test reads a tool's answer either way.
///
/// It deliberately skips the session check, the op-class floor and vault resolution: those are the
/// execute path's, they have their own coverage in <c>VaultShared.Tests</c>, and a tool-body test
/// that had to satisfy them could not exercise a caller whose grant lacks the tool's floor - which
/// is exactly what the attenuation cases need.
/// </summary>
internal static class DeclaredToolHarness
{
    internal sealed record Outcome(ToolResult? Result, ToolRefusal? Refusal)
    {
        public bool IsError => Refusal is not null;

        /// <summary>The structured result, serialized under the same options the front-end renders
        /// it with - so a test reads the wire names rather than the C# member names.</summary>
        public string Payload => JsonSerializer.Serialize(((ToolResult.Data)Result!).Value, NativeToolArguments.Options);

        /// <summary>The refusal's whole <c>{code, message, data?}</c> envelope.</summary>
        public string ErrorText => ToolRefusalEnvelope.Json(Refusal!);

        public string Code => Refusal!.Code;

        /// <summary>The refusal's <c>data</c> object as a name-to-raw-JSON map, or null when it
        /// carries none. Read off the rendered envelope, so the names are the wire's.</summary>
        public Dictionary<string, string>? Data
        {
            get
            {
                using var doc = JsonDocument.Parse(ErrorText);
                return doc.RootElement.TryGetProperty("data", out var data)
                    ? data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
                    : null;
            }
        }
    }

    public static Outcome Run(ITool tool, ToolDeclaration declaration, IReadOnlyDictionary<string, JsonElement> arguments, Grant grant, ClientSession? session = null)
    {
        var bound = NativeToolArguments.Bind(declaration.Arguments, arguments);
        try
        {
            return new Outcome(tool.Invoke(new ToolCall(bound, null, grant, session ?? new ClientSession("test-principal"))), null);
        }
        catch (ToolRefusal refusal)
        {
            return new Outcome(null, refusal);
        }
    }

    public static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
}
