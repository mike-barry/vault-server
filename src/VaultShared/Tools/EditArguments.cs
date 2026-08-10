using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>
/// Represents <see cref="EditTool"/>'s arguments: ordered frontmatter operations plus at most one
/// body change, applied as a single outcome.
/// </summary>
/// <remarks>
/// The operations stay structured on the wire, as a list of objects rather than a caller-encoded
/// string or a set of flattened sibling arguments. <see cref="OperationArgument"/> is the wire
/// projection of the frontmatter layer's four operations, and nothing between here and the write path
/// re-reads it as text.
/// </remarks>
/// <param name="Path">The vault-relative path of the note to edit.</param>
/// <param name="Vault">The vault the note is in, or null for the configured default.</param>
/// <param name="Operations">Frontmatter operations, applied in order, or null for none.</param>
/// <param name="Body">A change to the note's body, or null for none.</param>
/// <param name="ExpectedContentHash">The content hash a prior read returned. Required for a wholesale
/// body replacement, and accepted on any other edit, where it guards whatever that edit does.</param>
public sealed record EditArguments(
    [property: Description("Vault-relative path of the note, carrying the \".md\" extension. The extension is never inferred.")]
    string Path,
    [property: Description("The vault the note is in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("Frontmatter operations, applied in order, each seeing the previous one's result. Repetition is meaningful.")]
    IReadOnlyList<OperationArgument>? Operations = null,
    [property: Description("A change to the note's body: either a wholesale replacement, or a substitution that must match exactly once.")]
    BodyChange? Body = null,
    [property: Description("The content hash a prior read returned. Required for a wholesale body replacement, optional on any other edit, and the edit is refused if the file has changed since.")]
    string? ExpectedContentHash = null) : IVaultArguments
{
    /// <summary>
    /// The operations as sent, never null, so callers can enumerate without a null check. It sits
    /// beside <see cref="Operations"/> rather than replacing it, because the advertised schema derives
    /// from the record's own parameters and their order is the order a model reads them in.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<OperationArgument> Ordered => Operations ?? [];

    /// <summary>
    /// Checks everything decidable from the record alone: that the path is named, that the call
    /// changes something, that the body change is exactly one of its two forms, that a wholesale
    /// body replacement carries the staleness guard, and that every operation carries the arguments
    /// its own operation takes.
    /// </summary>
    /// <returns>The first fault found, or null.</returns>
    /// <remarks>
    /// The staleness guard's shape rule lives here rather than in the tool because it is a statement
    /// about the request alone. A wholesale replacement overwrites text the caller never saw and has
    /// nothing to verify itself against, so it must carry the hash. Every other edit may carry one,
    /// and it then guards whatever that edit does - a stale note refuses instead of applying.
    /// Comparing the hash against the file needs the note and happens in the tool.
    /// </remarks>
    public ArgumentFault? Validate()
    {
        if (ArgumentRules.Missing(Path, "path") is { } missing)
            return missing;

        // An edit naming neither an operation nor a body change is a read spelled as a mutation: it
        // would take the write lock, assert nothing, and return a hash.
        if (Ordered.Count == 0 && Body is null)
            return new ArgumentFault("operations", "An edit carries at least one operation or a body change.");

        if (Body?.Validate() is { } body)
            return body;

        if (Body?.Content is not null && ExpectedContentHash is null)
        {
            return new ArgumentFault(
                "expected_content_hash",
                "\"expected_content_hash\" is required for a wholesale body replacement - a replacement has no anchor to verify itself against. Take one from a read of the note.");
        }

        for (var i = 0; i < Ordered.Count; i++)
            if (Ordered[i].Validate(i) is { } operation)
                return operation;

        return null;
    }
}

/// <summary>
/// Represents a change to a note's body, in one of two forms. <see cref="Content"/> replaces the body
/// wholesale and requires a content hash. <see cref="Find"/> with <see cref="Replace"/> substitutes
/// once and verifies itself through its own anchor text.
/// </summary>
/// <param name="Content">The whole new body, replacing everything after the closing fence.</param>
/// <param name="Find">Text to substitute, which must occur exactly once in the body. Zero
/// occurrences and more than one are each refused rather than partly applied.</param>
/// <param name="Replace">What replaces the matched text. Empty text deletes it, which is why empty
/// is not a fault here.</param>
/// <remarks>
/// A call carries exactly one of the two forms. Merging them into one form with an optional anchor
/// would make the content-hash requirement depend on a value rather than on the request's shape.
/// </remarks>
public sealed record BodyChange(
    [property: Description("The whole new body, replacing everything after the frontmatter block. Requires \"expected_content_hash\".")]
    string? Content = null,
    [property: Description("Text to substitute, which must occur exactly once in the body. Zero matches and more than one are each refused.")]
    string? Find = null,
    [property: Description("What replaces the matched text. Empty deletes it. Sent with \"find\".")]
    string? Replace = null)
{
    /// <summary>Checks that exactly one of the two forms is present and complete.</summary>
    /// <returns>The fault, or null.</returns>
    internal ArgumentFault? Validate()
    {
        var wholesale = Content is not null;
        var substitution = Find is not null || Replace is not null;

        if (wholesale && substitution)
            return new ArgumentFault("body", "\"body\" carries a wholesale replacement and a substitution - it is one or the other.");
        if (!wholesale && !substitution)
            return new ArgumentFault("body", "\"body\" names no change - send \"content\" to replace the body, or \"find\" with \"replace\" to substitute once.");

        if (!wholesale)
        {
            if (OperationText.IsEmpty(Find))
                return new ArgumentFault("body", "\"find\" is required for a substitution, and empty text anchors nothing.");
            if (Replace is null)
                return new ArgumentFault("body", "\"replace\" is required with \"find\" - send empty text to delete what was matched.");
        }

        return null;
    }
}

/// <summary>
/// Represents one frontmatter operation on the wire. These four are the only operations, and this is
/// the only shape they arrive in. An entry's variant is never an <see cref="Op"/>. The payload states
/// its own variant, and the variants come from the vault's declaration rather than from a fixed set
/// of operation names.
/// </summary>
/// <remarks>
/// The same record is reused for an anchor, where <see cref="Op"/> means the placement and
/// <see cref="Target"/> means the entry the new one lands beside. Nothing else nests.
/// </remarks>
/// <param name="Op">One of <c>set</c>, <c>remove</c>, <c>add_entry</c>, <c>remove_entry</c> -
/// or, inside an anchor, <c>before</c> or <c>after</c>. Trimmed and case-folded before comparison,
/// and an unrecognized value is refused in this vocabulary rather than through the binder.</param>
/// <param name="Key">The frontmatter key being mutated.</param>
/// <param name="Value">The new value, for <c>set</c>.</param>
/// <param name="Entry">The entry to add, for <c>add_entry</c>: an object of the variant's fields, or
/// a bare string where the schema declares a scalar variant.</param>
/// <param name="Target">The identity of the entry to remove, for <c>remove_entry</c>; inside an
/// anchor, the identity of the entry the new one lands beside.</param>
/// <param name="Variant">The variant to clear wholesale, for <c>remove_entry</c>. Refused unless the
/// declaration says that variant may be cleared.</param>
/// <param name="Anchor">Where an added entry goes. Absent appends; placement is either explicit or an
/// append.</param>
public sealed record OperationArgument(
    [property: Description("The operation: \"set\", \"remove\", \"add_entry\" or \"remove_entry\". Inside an anchor, \"before\" or \"after\".")]
    string Op,
    [property: Description("The frontmatter key being mutated. Omitted inside an anchor, which addresses an entry under its operation's own key.")]
    string? Key = null,
    [property: Description("The new value. \"set\" only.")]
    string? Value = null,
    [property: Description("The entry to add: an object of the variant's fields, or a bare string for a scalar variant. \"add_entry\" only.")]
    JsonNode? Entry = null,
    [property: Description("The identity of the entry to remove, or - inside an anchor - of the entry the new one lands beside.")]
    string? Target = null,
    [property: Description("A variant to clear wholesale, with no target named. \"remove_entry\" only, and only where the declaration permits it.")]
    string? Variant = null,
    [property: Description("Where an added entry goes: an object carrying \"op\" of \"before\" or \"after\" and \"target\" naming an entry already in the list. Absent appends.")]
    OperationArgument? Anchor = null)
{
    /// <summary>The four operations, as they are spelled on the wire.</summary>
    internal const string SetOp = "set";
    internal const string RemoveOp = "remove";
    internal const string AddEntryOp = "add_entry";
    internal const string RemoveEntryOp = "remove_entry";

    /// <summary>The two placements an anchor spells, in the same member.</summary>
    internal const string BeforeOp = "before";
    internal const string AfterOp = "after";

    /// <summary>The operation name, trimmed and lower-cased once here so that no comparison site
    /// repeats the trimming and folding.</summary>
    private string Folded => Op?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Checks one operation's coherence from the record alone: that the operation is one of the four,
    /// that a key is named, and that every member this operation does not take is absent.
    /// </summary>
    /// <param name="index">Where in the caller's own list the operation sits, so a fault names an
    /// operation the caller can find.</param>
    /// <returns>The first fault found, or null.</returns>
    /// <remarks>
    /// An argument that cannot change the outcome is refused rather than ignored, so a value on a
    /// removal or a variant on an add is a fault rather than a member nothing reads.
    /// </remarks>
    internal ArgumentFault? Validate(int index)
    {
        var at = $"operations[{index}]";
        var op = Folded;

        if (op.Length == 0)
            return new ArgumentFault(at, $"\"{at}\" names no operation - it is one of \"{SetOp}\", \"{RemoveOp}\", \"{AddEntryOp}\", \"{RemoveEntryOp}\".");
        if (op is not (SetOp or RemoveOp or AddEntryOp or RemoveEntryOp))
            return new ArgumentFault(at, $"\"{at}\" names an unrecognized operation \"{Op}\" - it is one of \"{SetOp}\", \"{RemoveOp}\", \"{AddEntryOp}\", \"{RemoveEntryOp}\".");
        if (OperationText.IsEmpty(Key))
            return new ArgumentFault(at, $"\"{at}\" names no key.");

        if (Inert(at, Value, "value", op is SetOp, SetOp) is { } value)
            return value;
        if (Inert(at, Entry, "entry", op is AddEntryOp, AddEntryOp) is { } entry)
            return entry;
        if (Inert(at, Anchor, "anchor", op is AddEntryOp, AddEntryOp) is { } anchor)
            return anchor;
        if (Inert(at, Target, "target", op is RemoveEntryOp, RemoveEntryOp) is { } target)
            return target;
        if (Inert(at, Variant, "variant", op is RemoveEntryOp, RemoveEntryOp) is { } variant)
            return variant;

        switch (op)
        {
            case SetOp when Value is null:
                return new ArgumentFault(at, $"\"{at}\" is a \"{SetOp}\" and carries no \"value\".");

            case AddEntryOp when Entry is null:
                return new ArgumentFault(at, $"\"{at}\" is an \"{AddEntryOp}\" and carries no \"entry\".");
            case AddEntryOp when Entry is not (JsonObject or JsonValue):
                return new ArgumentFault(at, $"\"{at}\" has an \"entry\" that is neither an object of fields nor a bare value.");
            case AddEntryOp when Entry is JsonObject fields && FieldFault(at, fields) is { } field:
                return field;
            case AddEntryOp when Anchor?.ValidateAsAnchor(at) is { } placement:
                return placement;

            // An identity or a variant, never both and never neither. The two address entirely
            // different sets, and a removal naming neither addresses nothing.
            case RemoveEntryOp when (Target is null) == (Variant is null):
                return new ArgumentFault(
                    at,
                    Target is null
                        ? $"\"{at}\" is a \"{RemoveEntryOp}\" and names neither a \"target\" nor a \"variant\"."
                        : $"\"{at}\" names both a \"target\" and a \"variant\" - a removal addresses one entry by identity or one variant wholesale.");
        }

        return null;
    }

    /// <summary>
    /// Checks an anchor's coherence. An anchor reuses this record, so the only members it may carry
    /// are the placement in <see cref="Op"/> and the anchored entry in <see cref="Target"/>; its key
    /// is the owning operation's.
    /// </summary>
    /// <param name="operationAt">The owning operation's argument name, so a fault points at the
    /// anchor inside it.</param>
    /// <returns>The first fault found, or null.</returns>
    private ArgumentFault? ValidateAsAnchor(string operationAt)
    {
        var at = $"{operationAt}.anchor";
        var op = Folded;

        if (op is not (BeforeOp or AfterOp))
        {
            return new ArgumentFault(
                at,
                op.Length == 0
                    ? $"\"{at}\" names no placement - its \"op\" is \"{BeforeOp}\" or \"{AfterOp}\"."
                    : $"\"{at}\" names an unrecognized placement \"{Op}\" - it is \"{BeforeOp}\" or \"{AfterOp}\".");
        }

        if (OperationText.IsEmpty(Target))
            return new ArgumentFault(at, $"\"{at}\" names no \"target\" - an anchor addresses an entry already in the list.");

        // The anchor addresses an entry under the operation's own key, so a key here could only
        // disagree with it. The remaining members belong to operations an anchor is not.
        if (Key is not null)
            return new ArgumentFault(at, $"\"{at}\" carries a \"key\" - an anchor addresses an entry under its operation's own key.");
        if (Inert(at, Value, "value", permitted: false, SetOp) is { } value)
            return value;
        if (Inert(at, Entry, "entry", permitted: false, AddEntryOp) is { } entry)
            return entry;
        if (Inert(at, Variant, "variant", permitted: false, RemoveEntryOp) is { } variant)
            return variant;
        if (Inert(at, Anchor, "anchor", permitted: false, AddEntryOp) is { } nested)
            return nested;

        return null;
    }

    /// <summary>Refuses a member the current operation does not take.</summary>
    /// <param name="at">The argument name a fault points at.</param>
    /// <param name="value">The member's value.</param>
    /// <param name="member">The member's wire name.</param>
    /// <param name="permitted">Whether the current operation takes this member.</param>
    /// <param name="owner">The operation the member does belong to.</param>
    /// <returns>A fault when the member is present and not permitted, otherwise null.</returns>
    private static ArgumentFault? Inert(string at, object? value, string member, bool permitted, string owner) =>
        value is null || permitted
            ? null
            : new ArgumentFault(at, $"\"{at}\" carries a \"{member}\", which means nothing here - it belongs to \"{owner}\".");

    /// <summary>Checks that every field of a mapping entry is a name and a plain value.</summary>
    /// <param name="at">The argument name a fault points at.</param>
    /// <param name="fields">The entry's fields.</param>
    /// <returns>A fault when the entry has no fields or any field is a nested object or array,
    /// otherwise null. The emitter has no rendering for a nested shape.</returns>
    private static ArgumentFault? FieldFault(string at, JsonObject fields)
    {
        if (fields.Count == 0)
            return new ArgumentFault(at, $"\"{at}\" has an \"entry\" with no fields.");

        foreach (var (name, value) in fields)
            if (value is not JsonValue)
                return new ArgumentFault(at, $"\"{at}\" has an entry field \"{name}\" that is not a plain value.");

        return null;
    }

    /// <summary>
    /// Converts this wire operation into the frontmatter layer's own operation type. This is the only
    /// site that does so, so the write path never sees a caller's shape and no second reading of it
    /// can disagree with this one.
    /// </summary>
    /// <returns>The frontmatter operation. Total for any record <see cref="Validate"/> accepted.</returns>
    /// <exception cref="InvalidOperationException">The record was not validated first.</exception>
    public FrontmatterOperation Decode() => Folded switch
    {
        SetOp => new FrontmatterOperation.SetScalar(Key!, Value!),
        RemoveOp => new FrontmatterOperation.RemoveField(Key!),
        AddEntryOp => new FrontmatterOperation.AddEntry(Key!, Payload(Entry!), Anchor?.DecodeAnchor()),
        RemoveEntryOp => new FrontmatterOperation.RemoveEntry(
            Key!,
            Target is not null ? new EntryTarget.Identity(Target) : new EntryTarget.Variant(Variant!)),
        var other => throw new InvalidOperationException($"Unhandled operation \"{other}\" - Validate admits only the four."),
    };

    private EntryAnchor DecodeAnchor() =>
        new(Folded is AfterOp ? AnchorPlacement.After : AnchorPlacement.Before, Target!);

    /// <summary>Converts a wire entry into the frontmatter layer's payload.</summary>
    /// <param name="entry">The entry the caller sent.</param>
    /// <returns>The fields in the order they were sent, or a bare scalar value. The order is carried
    /// across because it is the order the emitter writes and the caller chose it.</returns>
    /// <remarks>
    /// A field's text is what the caller sent. A JSON string becomes that string; a number or boolean
    /// becomes its own literal text, because every frontmatter scalar is text in the note.
    /// </remarks>
    private static EntryPayload Payload(JsonNode entry) => entry switch
    {
        JsonObject fields => new EntryPayload.Mapping([.. fields.Select(f => new EntryFieldValue(f.Key, Text(f.Value!)))]),
        _ => new EntryPayload.Scalar(Text(entry)),
    };

    private static string Text(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
}
