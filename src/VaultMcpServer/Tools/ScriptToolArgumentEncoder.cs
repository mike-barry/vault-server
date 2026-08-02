using System.Text.Encodings.Web;
using System.Text.Json;

namespace VaultMcpServer.Tools;

/// <summary>
/// The call boundary between a client's JSON argument and the value that goes into the splat
/// hashtable <see cref="ScriptToolRunner"/> hands the script.
///
/// Tool scripts keep <c>[string]</c> parameters even where the contract is an array of objects,
/// so that a direct <c>pwsh -File</c> invocation passing that argument as a JSON string keeps
/// working unchanged and the encoding never enters PowerShell at all. The host therefore JSON-encodes a structured
/// argument on the way in, for exactly the parameters whose side-car schema says they carry one -
/// see <see cref="ScriptToolParameter.EncodesStructuredArgument"/> for the (derived, never
/// hand-listed) signal.
///
/// Two rules, and the second is the one that is easy to get wrong:
/// <list type="number">
/// <item><description><b>Only a structured arrival value is serialized.</b> An argument that
/// arrives as a JSON string passes through verbatim, whatever its parameter's schema says.
/// Without this, a caller sending an already-encoded payload against an array-typed schema gets
/// it encoded a second time and the script parses a JSON <em>string</em> where it expected an
/// array.</description></item>
/// <item><description><b>Everything else converts exactly as before.</b> A parameter with no
/// side-car, or one whose side-car declares <c>type: "string"</c>, takes the same path it took
/// before this existed.</description></item>
/// </list>
///
/// Held as a separate helper rather than inlined into <see cref="ScriptToolDispatcher"/> because
/// the same rule applies one level up, to a computed remedy payload naming another tool: the
/// remedy carries structure and the dispatch boundary serializes it, by this function, against
/// that tool's own parameters.
/// </summary>
public static class ScriptToolArgumentEncoder
{
    /// <summary>
    /// Encoder options for the payload string a tool script will itself parse.
    ///
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is chosen deliberately over the
    /// default. The default encoder is built for JSON that might be interpolated into HTML or
    /// script, so it escapes every non-ASCII character to <c>\uXXXX</c> and also escapes
    /// <c>+</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c> and <c>`</c>. That is still
    /// valid JSON and still round-trips through <c>ConvertFrom-Json</c>, but this string is never
    /// HTML - it goes straight into a PowerShell parameter - so the escaping buys nothing and
    /// costs on both ends: a vault path or a frontmatter value carrying an accent, a CJK
    /// character, or an <c>&amp;</c> reaches the script (and any error text or log line echoing
    /// it) as an unreadable escape run rather than as what the caller sent. Relaxed escaping
    /// still escapes <c>"</c>, <c>\</c>, and the control characters - the ones JSON itself
    /// requires - so the output is valid JSON either way.
    ///
    /// Not indented: the value is a machine-parsed payload, and indentation would only add bytes.
    /// </summary>
    private static readonly JsonSerializerOptions EncodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// The splat value for one client-supplied argument: the JSON text of
    /// <paramref name="value"/> when <paramref name="parameter"/> is an encoding parameter and the
    /// value actually arrived structured, and the plain .NET conversion otherwise.
    /// </summary>
    /// <param name="parameter">The advertised parameter the argument names, or null when the
    /// caller has no parameter to match it against (in which case it converts plainly).</param>
    public static object? BindArgument(ScriptToolParameter? parameter, JsonElement value) =>
        parameter is { EncodesStructuredArgument: true } && value.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? JsonSerializer.Serialize(value, EncodeOptions)
            : ConvertJsonElement(value);

    /// <summary>
    /// The plain JSON-to-.NET conversion for a splat value: scalars to their CLR equivalents,
    /// arrays to <c>object?[]</c>, and objects to <c>Dictionary&lt;string, object?&gt;</c>, which
    /// PowerShell binds as its own array and hashtable. Unchanged behavior - it is what every
    /// argument got before encoding existed, and what every non-encoding argument still gets.
    ///
    /// The <c>(object)</c> on the number branch's integral arm is load-bearing. A conditional
    /// expression takes its operands' best common type, and <c>long</c> converts implicitly to
    /// <c>double</c> while the reverse does not - so written as
    /// <c>TryGetInt64(out var l) ? l : GetDouble()</c> the whole expression is <c>double</c> and
    /// the integral arm is widened away, leaving <c>TryGetInt64</c> computed and discarded. Boxing
    /// each arm separately keeps the branch that actually ran. Past 2^53 that is not cosmetic: a
    /// double cannot hold consecutive integers there, so the widening silently altered the value.
    /// </summary>
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
