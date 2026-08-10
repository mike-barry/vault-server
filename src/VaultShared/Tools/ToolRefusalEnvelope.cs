using System.Text;
using System.Text.Json;
using VaultShared.Access;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>
/// Writes the shared <c>{code, message, data?}</c> refusal envelope, and translates refusals raised
/// in the other layers' vocabularies into this one. A caller branches on <c>code</c> whichever layer
/// refused.
/// </summary>
/// <remarks>
/// <para>
/// Both front-ends emit this envelope - the MCP server inside an <c>isError</c> tool result, the CLI
/// on stderr - so the writer lives here and neither restates it.
/// </para>
/// <para>
/// The wire form is fixed and callers depend on it: no whitespace, <c>code</c> then <c>message</c>
/// then <c>data</c>, and <c>data</c> left out of the object entirely rather than written as null
/// when there is none, so a caller can test whether the field is present. The code writes with
/// <see cref="Utf8JsonWriter"/> instead of serializing an anonymous envelope type so that field
/// order is pinned here rather than left to reflection order.
/// </para>
/// <para>
/// <c>data</c> is serialized with <see cref="NativeToolArguments.Options"/>, the same options the
/// argument binder and the result serializer use, so one naming policy governs the wire in both
/// directions.
/// </para>
/// </remarks>
public static class ToolRefusalEnvelope
{
    /// <summary>Returns the envelope as a compact JSON string.</summary>
    /// <param name="code">The code from <see cref="ToolCodes"/>.</param>
    /// <param name="message">The caller-visible message.</param>
    /// <param name="data">The payload, or null to omit the field.</param>
    /// <returns>The serialized envelope.</returns>
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
    /// Returns the refusal an invocation raised, expressed in this vocabulary, or null where the
    /// exception is not a refusal and the front-end should let it propagate.
    /// </summary>
    /// <param name="exception">The exception an invocation raised.</param>
    /// <returns>The translated refusal, or null.</returns>
    /// <remarks>
    /// The set of refusal types both front-ends recognize is listed here and not in each front-end's
    /// rendering path. A fourth refusal type added to one front-end's catch and not the other would
    /// crash one front-end and return an envelope from the other, for the same call.
    /// </remarks>
    public static ToolRefusal? Refusal(Exception exception) => exception switch
    {
        ToolRefusal refusal => refusal,
        FrontmatterRefusal refusal => Translate(refusal),
        AuthorityRefusal refusal => Translate(refusal),
        _ => null,
    };

    /// <summary>
    /// Maps a <see cref="FrontmatterRefusal"/>'s <see cref="RefusalReason"/> onto a code from
    /// <see cref="ToolCodes"/>. This is the only site that does so, and it runs where the envelope is
    /// rendered rather than in a tool's catch block, so no tool has to reason about a refusal it did
    /// not raise.
    /// </summary>
    /// <param name="refusal">The refusal the frontmatter layer raised.</param>
    /// <returns>The refusal in this vocabulary. Its payload carries the refusal's own coordinates -
    /// the keys it locates, the index of the operation that owns them, and the line and column at
    /// which text the caller supplied failed to parse, all of which came from the caller - and, for
    /// an undeclared key, the vault's declared key names. Nothing about the grant reaches
    /// it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason has no declared wire code.</exception>
    public static ToolRefusal Translate(FrontmatterRefusal refusal)
    {
        var code = refusal.Reason switch
        {
            // Whose text failed decides the code. An unparsable stored block is the note's problem;
            // an unparsable supplied block is the caller's, and the remedies differ.
            RefusalReason.Unparsable => ToolCodes.FrontmatterUnparsable,
            RefusalReason.NotAMapping => ToolCodes.FrontmatterUnparsable,
            RefusalReason.SuppliedBlockInvalid => ToolCodes.InvalidArgument,

            RefusalReason.FenceConflict => ToolCodes.FenceConflict,
            RefusalReason.PathExcluded => ToolCodes.PathExcluded,

            // A control character and a value the declaration refuses both mean the declaration will
            // not take this text, and both have the same remedy: send a different value.
            RefusalReason.ControlCharacter => ToolCodes.InvalidValue,
            RefusalReason.ValueRefused => ToolCodes.InvalidValue,

            RefusalReason.UnknownKey => ToolCodes.UnknownKey,
            RefusalReason.ShapeMismatch => ToolCodes.ShapeMismatch,
            RefusalReason.UnresolvedIdentity => ToolCodes.EntryNotFound,
            RefusalReason.AmbiguousIdentity => ToolCodes.EntryAmbiguous,

            // The remedy is to name entries one at a time, so the shape of the request is what has
            // to change, which is what invalid_argument means.
            RefusalReason.WholesaleRemovalRefused => ToolCodes.InvalidArgument,

            RefusalReason.AuthorityDenied => ToolCodes.AuthorityRequired,
            RefusalReason.RegistryUnavailable => ToolCodes.FrontmatterUnavailable,

            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Reason, "No wire code is declared for this refusal reason."),
        };

        return new ToolRefusal(code, refusal.Message, Payload(refusal));
    }

    /// <summary>
    /// Maps the access layer's answer for a caller-named path onto a code. This is the only site that
    /// does so. The layer distinguishes the three refusals structurally and does not name them, so no
    /// tool re-decides that a dot segment is <see cref="ToolCodes.PathExcluded"/> rather than a scope
    /// denial.
    /// </summary>
    /// <typeparam name="TPath">The resolved path type the layer returns.</typeparam>
    /// <param name="resolution">A refused path resolution.</param>
    /// <returns>The refusal in this vocabulary. Each message repeats only the caller's own path.
    /// <see cref="ToolCodes.OutOfScope"/> carries no payload, because its only remedy is elevation
    /// and there is no corrected call to lift a value into.</returns>
    /// <exception cref="ArgumentException"><paramref name="resolution"/> admitted the path, which is
    /// not a refusal to translate.</exception>
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
    /// Maps an enumeration or content-search failure onto a code. This is the only site that does so.
    /// A malformed pattern the caller supplied is <see cref="ToolCodes.InvalidPattern"/> and needs
    /// the call to change; an engine that could not be started is
    /// <see cref="ToolCodes.DependencyUnavailable"/> and no retry of the identical call can succeed;
    /// every other failure is <see cref="ToolCodes.SearchFailed"/> and the identical call may be
    /// retried.
    /// </summary>
    /// <typeparam name="TResult">The search result type.</typeparam>
    /// <param name="outcome">A failed search outcome.</param>
    /// <returns>The refusal in this vocabulary.</returns>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is a result, not a
    /// failure.</exception>
    /// <remarks>
    /// An engine that could not be started carries no payload. The platform's message names the
    /// executable the host configured, which is a server-side path the caller may not read and cannot
    /// act on.
    ///
    /// A file the search could not read is a search failure, not a read failure.
    /// <see cref="ToolCodes.ReadFailed"/> names a path in its payload, and the file that failed is
    /// one the engine met while walking the whole vault, before scope filtering. Naming it would
    /// disclose a path the caller may not reach, through a surface that otherwise filters silently.
    /// The engine's diagnostic goes into <c>detail</c> with that path scrubbed out, and both
    /// failures are retryable in the same way.
    /// </remarks>
    public static ToolRefusal Translate<TResult>(SearchOutcome<TResult> outcome) =>
        outcome switch
        {
            SearchOutcome<TResult>.BadPattern bad => new ToolRefusal(
                ToolCodes.InvalidPattern,
                $"The search engine refused the pattern: {bad.Reason}"),

            SearchOutcome<TResult>.Unreadable unreadable => new ToolRefusal(
                ToolCodes.SearchFailed,
                "The search failed because a file it had to read could not be read.",
                new { detail = unreadable.Reason.Replace(unreadable.Path, "<unreadable file>", StringComparison.OrdinalIgnoreCase) }),

            SearchOutcome<TResult>.Failed failed => new ToolRefusal(
                ToolCodes.SearchFailed,
                "The search engine failed.",
                new { detail = failed.Reason }),

            SearchOutcome<TResult>.EngineUnavailable => new ToolRefusal(
                ToolCodes.DependencyUnavailable,
                "The search engine could not be started, so no search or enumeration can run until the deployment is corrected."),

            _ => throw new ArgumentException("A result is not a refusal.", nameof(outcome)),
        };

    /// <summary>
    /// Translates the denial of a key assertion made mid-operation, which the access layer raises in
    /// its own vocabulary.
    /// </summary>
    /// <param name="refusal">The denial the access layer raised.</param>
    /// <returns>The refusal in this vocabulary, with no payload. The message names the op class the
    /// operation turned out to require, and the caller's remedy is elevation rather than a corrected
    /// call.</returns>
    public static ToolRefusal Translate(AuthorityRefusal refusal) =>
        new(ToolCodes.AuthorityRequired, refusal.Message);

    /// <summary>
    /// Returns the refusal's payload: the coordinates it locates, and the declared key names where
    /// it carries them. Null where it carries none of these.
    /// </summary>
    /// <param name="refusal">The refusal to read the payload from.</param>
    /// <returns>The payload, or null when there is nothing to report.</returns>
    /// <remarks>
    /// A dictionary rather than an anonymous type, because every member is conditional and an
    /// absent member must not appear as null. Its keys are written verbatim, so they are spelled
    /// <c>snake_case</c> here to match the rest of the wire vocabulary.
    /// </remarks>
    private static object? Payload(FrontmatterRefusal refusal)
    {
        var data = new Dictionary<string, object>();
        if (refusal.Keys.Count > 0)
            data["keys"] = refusal.Keys;
        if (refusal.Operation is { } operation)
            data["operation"] = operation;
        // Both members go in together, because a line without a column locates a whole line.
        if (refusal.Position is { } position)
        {
            data["line"] = position.Line;
            data["column"] = position.Column;
        }
        // The declared keys are the vault's own declared vocabulary and are not filtered against
        // what this caller may write, so listing them tells the caller nothing about its grant.
        if (refusal.DeclaredKeys.Count > 0)
            data["declared_keys"] = refusal.DeclaredKeys;
        return data.Count > 0 ? data : null;
    }
}
