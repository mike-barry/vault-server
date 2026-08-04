using System.Text.Json;
using VaultShared.Auth;
using VaultShared.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Invokes a declared tool's body directly, without the shared execute path in front of it.
/// Arguments are bound through the declaration exactly as the execute path binds them, and a
/// refusal is caught rather than propagated so a test can read either outcome the same way.
/// </summary>
/// <remarks>
/// The session check, the op-class floor and vault resolution are skipped. They belong to the
/// execute path and are covered by <c>VaultShared.Tests</c>, and a tool-body test that had to
/// satisfy the floor could not exercise a caller whose grant sits below it - which is what the
/// attenuation cases need.
/// </remarks>
internal static class DeclaredToolHarness
{
    /// <summary>Represents what a tool invocation produced: a result or a refusal.</summary>
    /// <param name="Result">The tool's result, or null when it refused.</param>
    /// <param name="Refusal">The refusal the tool threw, or null when it returned a result.</param>
    internal sealed record Outcome(ToolResult? Result, ToolRefusal? Refusal)
    {
        /// <summary>Gets a value indicating whether the tool refused.</summary>
        public bool IsError => Refusal is not null;

        /// <summary>
        /// Gets the result serialized under the same options the front-end renders it with, so a
        /// test reads the wire names rather than the C# member names.
        /// </summary>
        public string Payload => JsonSerializer.Serialize(((ToolResult.Data)Result!).Value, NativeToolArguments.Options);

        /// <summary>Gets the refusal's whole <c>{code, message, data?}</c> envelope as JSON.</summary>
        public string ErrorText => ToolRefusalEnvelope.Json(Refusal!);

        /// <summary>Gets the refusal's error code.</summary>
        public string Code => Refusal!.Code;

        /// <summary>
        /// Gets the refusal's <c>data</c> object as a name-to-raw-JSON map, or null when it carries
        /// none. Read off the rendered envelope, so the names are the wire names.
        /// </summary>
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

    /// <summary>Binds the arguments and invokes the tool, capturing a refusal instead of throwing.</summary>
    /// <param name="tool">The tool to invoke.</param>
    /// <param name="declaration">The declaration the arguments are bound through.</param>
    /// <param name="arguments">The raw JSON arguments, keyed by wire name.</param>
    /// <param name="grant">The grant the call runs under.</param>
    /// <param name="session">The calling session, or null to use a default test session.</param>
    /// <returns>The tool's result or the refusal it threw.</returns>
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

    /// <summary>Converts an anonymous object into the argument map <see cref="Run"/> expects.</summary>
    /// <param name="value">An object whose properties are the tool arguments.</param>
    /// <returns>The properties as a map of wire name to JSON value.</returns>
    public static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
}
