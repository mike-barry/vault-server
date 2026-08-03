using System.Text;
using System.Text.Json;
using VaultShared.Access;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>
/// The shared <c>{code, message, data?}</c> envelope, and the one table translating refusals raised
/// in another vocabulary into this one.
///
/// The invariant it protects: a caller branches on <c>code</c> regardless of which layer refused.
/// Both front-ends emit the envelope - the MCP server inside an <c>isError</c> tool result, the CLI
/// on stderr - so the writer lives here and neither restates it.
///
/// <b>The wire form is fixed:</b> no whitespace, <c>code</c> then <c>message</c> then <c>data</c>,
/// and <c>data</c> omitted from the object entirely (not emitted as null) when there is none, so a
/// caller can test for the field's presence rather than its nullness. <see cref="Utf8JsonWriter"/>
/// is used directly rather than serializing an anonymous envelope type, because field order has to
/// be pinned by the writer rather than left to reflection ordering.
///
/// <c>data</c> is serialized under <see cref="NativeToolArguments.Options"/>, the same options the
/// binder and the result serializer use, so one naming policy governs everything on the wire in
/// both directions.
/// </summary>
public static class ToolRefusalEnvelope
{
    /// <summary>The envelope as a compact string.</summary>
    public static string Json(string code, string message, object? data = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            if (data is not null)
            {
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, data, data.GetType(), NativeToolArguments.Options);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc cref="Json(string, string, object?)"/>
    public static string Json(ToolRefusal refusal) => Json(refusal.Code, refusal.Message, refusal.Data);

    /// <summary>
    /// The single site that maps a <see cref="FrontmatterRefusal"/>'s <see cref="RefusalReason"/>
    /// onto a code from <see cref="ToolCodes"/>. Applied where the envelope is rendered rather than
    /// in any tool's catch, so no tool becomes a translation point reasoning about a refusal it did
    /// not raise.
    ///
    /// The payload carries the refusal's own coordinates - the keys it locates, and the index of the
    /// operation that owns it - both of which are values the caller supplied. Nothing about the
    /// grant reaches it.
    /// </summary>
    public static ToolRefusal Translate(FrontmatterRefusal refusal)
    {
        var code = refusal.Reason switch
        {
            // Whose text it is decides the code: a stored block nobody can parse is the note's
            // problem, a supplied one is the caller's, and the remedies differ.
            RefusalReason.Unparsable => ToolCodes.FrontmatterUnparsable,
            RefusalReason.NotAMapping => ToolCodes.FrontmatterUnparsable,
            RefusalReason.SuppliedBlockInvalid => ToolCodes.InvalidArgument,

            RefusalReason.FenceConflict => ToolCodes.FenceConflict,
            RefusalReason.PathExcluded => ToolCodes.PathExcluded,

            // A control character and a refused value are both "the declaration will not take this
            // text", which is one remedy: send a different value.
            RefusalReason.ControlCharacter => ToolCodes.InvalidValue,
            RefusalReason.ValueRefused => ToolCodes.InvalidValue,

            RefusalReason.UnknownKey => ToolCodes.UnknownKey,
            RefusalReason.ShapeMismatch => ToolCodes.ShapeMismatch,
            RefusalReason.UnresolvedIdentity => ToolCodes.EntryNotFound,
            RefusalReason.AmbiguousIdentity => ToolCodes.EntryAmbiguous,

            // The remedy is to name entries one at a time, so the request's shape is what has to
            // change - which is what invalid_argument means.
            RefusalReason.WholesaleRemovalRefused => ToolCodes.InvalidArgument,

            RefusalReason.AuthorityDenied => ToolCodes.AuthorityRequired,
            RefusalReason.RegistryUnavailable => ToolCodes.FrontmatterUnavailable,

            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Reason, "No wire code is declared for this refusal reason."),
        };

        return new ToolRefusal(code, refusal.Message, Coordinates(refusal));
    }

    /// <summary>
    /// The mid-operation key assertion's denial, which the layer raises in its own vocabulary. It
    /// carries no payload: the class the operation turned out to require is named in the message,
    /// and a caller's remedy is elevation rather than a corrected call.
    /// </summary>
    public static ToolRefusal Translate(AuthorityRefusal refusal) =>
        new(ToolCodes.AuthorityRequired, refusal.Message);

    /// <summary>The refusal's own coordinates, or null where it locates neither a key nor an
    /// operation. A dictionary rather than an anonymous type, because both members are conditional
    /// and an absent member must not appear as null.</summary>
    private static object? Coordinates(FrontmatterRefusal refusal)
    {
        var data = new Dictionary<string, object>();
        if (refusal.Keys.Count > 0)
            data["keys"] = refusal.Keys;
        if (refusal.Operation is { } operation)
            data["operation"] = operation;
        return data.Count > 0 ? data : null;
    }
}
