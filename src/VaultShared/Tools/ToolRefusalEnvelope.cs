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
    /// The refusal an invocation raised, in this vocabulary, whichever vocabulary it was raised in -
    /// or null where <paramref name="exception"/> is not a refusal at all and the front-end should
    /// let it travel.
    ///
    /// Both front-ends catch exactly these three, so which three they are is stated here rather than
    /// in each of their rendering paths: a fourth refusal vocabulary added to one catch and not the
    /// other would surface as a crash in one front-end and an envelope in the other, for the same
    /// call.
    /// </summary>
    public static ToolRefusal? Refusal(Exception exception) => exception switch
    {
        ToolRefusal refusal => refusal,
        FrontmatterRefusal refusal => Translate(refusal),
        AuthorityRefusal refusal => Translate(refusal),
        _ => null,
    };

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
    /// The single site that maps the layer's answer to a caller-named path onto a code. The layer
    /// distinguishes the three refusals structurally and says nothing about what they are called;
    /// this is where they are named, once, so no tool re-decides that a dot segment is
    /// <see cref="ToolCodes.PathExcluded"/> rather than a scope denial.
    ///
    /// Each payload is the caller's own text and nothing else. <see cref="ToolCodes.OutOfScope"/>
    /// carries none at all: its only remedy is elevation, so there is no corrected call to lift a
    /// value into.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="resolution"/> was admitted, which is not
    /// a refusal to translate.</exception>
    public static ToolRefusal Translate<TPath>(PathResolution<TPath> resolution) where TPath : class =>
        resolution switch
        {
            PathResolution<TPath>.Malformed malformed => new ToolRefusal(
                ToolCodes.InvalidPath,
                $"\"{malformed.Path}\" is not a usable vault-relative path: {malformed.Reason}"),

            PathResolution<TPath>.Excluded excluded => new ToolRefusal(
                ToolCodes.PathExcluded,
                $"\"{excluded.Path}\" lies under a dot-directory, which this tooling does not own."),

            PathResolution<TPath>.OutOfScope outOfScope => new ToolRefusal(
                ToolCodes.OutOfScope,
                $"\"{outOfScope.Path}\" is outside this grant's scope."),

            _ => throw new ArgumentException("An admitted path is not a refusal.", nameof(resolution)),
        };

    /// <summary>
    /// The single site that maps an enumeration or content-search failure onto a code. Two of the
    /// layer's four outcomes are failures the catalogue names apart, and the split is the one the
    /// specs require to survive: a caller's own malformed pattern is
    /// <see cref="ToolCodes.InvalidPattern"/> and needs the call to change, everything else is
    /// <see cref="ToolCodes.SearchFailed"/> and the identical call may be retried.
    ///
    /// <b>A file the search could not read is a search failure, not a read failure.</b>
    /// <see cref="ToolCodes.ReadFailed"/> names a path in its payload, and the file that failed is
    /// one the engine met while walking the whole vault - before scope filtering - so naming it
    /// would disclose a path the caller may not reach through a surface that otherwise filters
    /// silently. The engine's diagnostic rides <c>detail</c> with that entry scrubbed out of it,
    /// and both failures are retryable in the same way.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is a result, not a
    /// failure.</exception>
    public static ToolRefusal Translate<TResult>(SearchOutcome<TResult> outcome) =>
        outcome switch
        {
            SearchOutcome<TResult>.BadPattern bad => new ToolRefusal(
                ToolCodes.InvalidPattern,
                $"The search engine refused the pattern: {bad.Reason}"),

            // The diagnostic is scrubbed of the entry it names before it rides detail: the file the
            // engine met is found while walking the whole vault, before scope filtering, so carrying
            // its path would disclose one the caller may not reach through a surface that otherwise
            // filters silently.
            SearchOutcome<TResult>.Unreadable unreadable => new ToolRefusal(
                ToolCodes.SearchFailed,
                "The search failed because a file it had to read could not be read.",
                new { detail = unreadable.Reason.Replace(unreadable.Path, "<unreadable file>", StringComparison.OrdinalIgnoreCase) }),

            SearchOutcome<TResult>.Failed failed => new ToolRefusal(
                ToolCodes.SearchFailed,
                "The search engine failed.",
                new { detail = failed.Reason }),

            _ => throw new ArgumentException("A result is not a refusal.", nameof(outcome)),
        };

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
