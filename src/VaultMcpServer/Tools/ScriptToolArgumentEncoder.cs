using System.Text.Encodings.Web;
using System.Text.Json;

namespace VaultMcpServer.Tools;

/// <summary>
/// Converts a client's JSON argument into the value that goes into the splat hashtable
/// <see cref="ScriptToolRunner"/> hands the script.
/// </summary>
/// <remarks>
/// <para>
/// Tool scripts keep <c>[string]</c> parameters even where the contract is an array of objects,
/// so that a direct <c>pwsh -File</c> invocation passing that argument as a JSON string works
/// unchanged. The host therefore JSON-encodes a structured argument on the way in, for exactly
/// the parameters whose side-car schema says they carry one - see
/// <see cref="ScriptToolParameter.EncodesStructuredArgument"/>.
/// </para>
/// <para>
/// Only a value that arrives structured is serialized. An argument that arrives as a JSON string
/// passes through verbatim whatever its parameter's schema says, because otherwise a caller
/// sending an already-encoded payload against an array-typed schema would have it encoded a
/// second time and the script would parse a JSON string where it expected an array. Everything
/// else - a parameter with no side-car, or one whose side-car declares <c>type: "string"</c> -
/// takes the plain conversion path.
/// </para>
/// <para>
/// This is a separate helper rather than part of <see cref="ScriptToolDispatcher"/> because the
/// same rule applies to a computed remedy payload naming another tool, which is serialized here
/// against that tool's own parameters.
/// </para>
/// </remarks>
public static class ScriptToolArgumentEncoder
{
    /// <summary>
    /// The serializer options for the payload string a tool script parses itself.
    /// </summary>
    /// <remarks>
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is used instead of the default
    /// encoder. The default is built for JSON that might be interpolated into HTML or script, so
    /// it escapes every non-ASCII character to <c>\uXXXX</c> along with <c>+</c>, <c>&lt;</c>,
    /// <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, and <c>`</c>. That is valid JSON and still parses
    /// through <c>ConvertFrom-Json</c>, but this string goes straight into a PowerShell
    /// parameter and is never HTML, so the escaping only makes a vault path or frontmatter value
    /// carrying an accent, a CJK character, or an <c>&amp;</c> reach the script - and any error
    /// text or log line echoing it - as an unreadable escape run. Relaxed escaping still escapes
    /// <c>"</c>, <c>\</c>, and the control characters, which JSON itself requires, so the output
    /// is valid either way.
    ///
    /// Indentation is off because the value is machine-parsed and would only grow.
    /// </remarks>
    private static readonly JsonSerializerOptions EncodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// Returns the splat value for one client-supplied argument: the JSON text of the value when
    /// the parameter encodes structured arguments and the value arrived as an array or an
    /// object, and the plain .NET conversion otherwise.
    /// </summary>
    /// <param name="parameter">The advertised parameter the argument names, or null when the
    /// caller has no parameter to match it against, in which case it converts plainly.</param>
    /// <param name="value">The argument value as it arrived from the client.</param>
    /// <returns>The value to place in the splat hashtable.</returns>
    public static object? BindArgument(ScriptToolParameter? parameter, JsonElement value) =>
        parameter is { EncodesStructuredArgument: true } && value.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? JsonSerializer.Serialize(value, EncodeOptions)
            : ConvertJsonElement(value);

    /// <summary>
    /// Converts a JSON value to its plain .NET equivalent: scalars to their CLR types, arrays to
    /// <c>object?[]</c>, and objects to <c>Dictionary&lt;string, object?&gt;</c>, which
    /// PowerShell binds as its own array and hashtable.
    /// </summary>
    /// <remarks>
    /// Do not remove the <c>(object)</c> cast on the number branch's integral arm. A conditional
    /// expression takes its operands' best common type, and <c>long</c> converts implicitly to
    /// <c>double</c> while the reverse does not, so <c>TryGetInt64(out var l) ? l :
    /// GetDouble()</c> would be a <c>double</c> expression and the integral result would be
    /// discarded. Above 2^53 a <c>double</c> cannot hold consecutive integers, so that widening
    /// would silently change the value. Boxing each arm separately keeps the branch that ran.
    /// </remarks>
    /// <param name="element">The JSON value to convert.</param>
    /// <returns>The .NET equivalent, or null for a JSON null or an undefined element.</returns>
    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
        _ => null,
    };
}
