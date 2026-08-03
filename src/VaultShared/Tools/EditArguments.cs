using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>
/// <see cref="EditTool"/>'s arguments: ordered frontmatter operations plus at most one body change,
/// applied as a single outcome.
///
/// <b>The operations stay structured.</b> An argument whose contract is an ordered list of
/// operations is that on the wire - not a caller-encoded string, not flattened into siblings - so
/// <see cref="OperationArgument"/> is the wire projection of the substrate's four capabilities and
/// nothing between here and the write path re-reads it as text.
/// </summary>
public sealed record EditArguments(
    [property: Description("Vault-relative path of the note, carrying the \".md\" extension. The extension is never inferred.")]
    string Path,
    [property: Description("The vault the note is in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("Frontmatter operations, applied in order, each seeing the previous one's result. Repetition is meaningful.")]
    IReadOnlyList<OperationArgument>? Operations = null,
    [property: Description("A change to the note's body: either a wholesale replacement, or a substitution that must match exactly once.")]
    BodyChange? Body = null,
    [property: Description("The content hash a prior read returned. Required for a wholesale body replacement, refused otherwise, and refused if the file has changed since.")]
    string? ExpectedContentHash = null) : IVaultArguments
{
    /// <summary>
    /// The operations as sent, never null - every caller enumerates them, and an absent list is an
    /// empty batch rather than a null check in each of them. It sits beside the member rather than
    /// replacing it, because the schema derives from the record's own parameters and their order is
    /// the order a model reads them in.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<OperationArgument> Ordered => Operations ?? [];

    /// <summary>
    /// Everything decidable from the record alone: the operand is named, the call changes
    /// something, the body change is one of its two forms, the staleness guard is present exactly
    /// where it can act, and every operation carries the arguments its own capability takes.
    ///
    /// The staleness guard's two rules are both here rather than in the tool's own plan phase,
    /// because both are statements about the shape of the request: a wholesale replacement has
    /// nothing to self-verify against and so needs the hash, and a hash with no wholesale
    /// replacement cannot change an outcome. Comparing the hash against the file is the plan
    /// phase's, and it is the only half that needs the note.
    /// </summary>
    public ArgumentFault? Validate()
    {
        if (ArgumentRules.Missing(Path, "path") is { } missing)
            return missing;

        // An edit naming neither an operation nor a body change is a read spelled as a mutation: it
        // takes the lock, asserts nothing, and answers a hash. Refused rather than admitted, on the
        // rule that an argument surface which cannot change an outcome is refused rather than
        // ignored.
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

        if (Body?.Content is null && ExpectedContentHash is not null)
        {
            return new ArgumentFault(
                "expected_content_hash",
                "\"expected_content_hash\" guards a wholesale body replacement and nothing else - a substitution self-verifies through its own anchor, and a frontmatter operation addresses what it names.");
        }

        for (var i = 0; i < Ordered.Count; i++)
            if (Ordered[i].Validate(i) is { } operation)
                return operation;

        return null;
    }
}

/// <summary>
/// The two forms of body change, distinguished because the staleness rule distinguishes them.
/// <see cref="Content"/> replaces the body wholesale and requires a content hash;
/// <see cref="Find"/> with <see cref="Replace"/> substitutes once and self-verifies through its own
/// anchor.
///
/// Exactly one form. One form with an optional anchor would make the hash requirement conditional
/// on a value rather than on a shape.
/// </summary>
/// <param name="Content">The whole new body, replacing everything after the closing fence.</param>
/// <param name="Find">Text to substitute, which must occur exactly once in the body. Zero
/// occurrences and more than one are each a refusal rather than a partial application.</param>
/// <param name="Replace">What replaces it. Empty deletes the matched text, which is why its
/// emptiness is not a fault.</param>
public sealed record BodyChange(
    [property: Description("The whole new body, replacing everything after the frontmatter block. Requires \"expected_content_hash\".")]
    string? Content = null,
    [property: Description("Text to substitute, which must occur exactly once in the body. Zero matches and more than one are each refused.")]
    string? Find = null,
    [property: Description("What replaces the matched text. Empty deletes it. Sent with \"find\".")]
    string? Replace = null)
{
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
/// One frontmatter operation on the wire: the projection of the substrate's four capabilities, and
/// the only shape they arrive in. An entry kind is not an <see cref="Op"/> - the payload states its
/// own variant, and the variants come from the vault's declaration rather than from a set of
/// operation names.
///
/// The record is also the anchor's shape, where its members mean the placement and the entry the new
/// one lands beside. Nothing else nests.
/// </summary>
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
/// <param name="Anchor">Where an added entry goes. Absent appends: placement is explicit or it is an
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
    /// <summary>The four capabilities, as they are spelled on the wire.</summary>
    internal const string SetOp = "set";
    internal const string RemoveOp = "remove";
    internal const string AddEntryOp = "add_entry";
    internal const string RemoveEntryOp = "remove_entry";

    /// <summary>The two placements an anchor spells, in the same member.</summary>
    internal const string BeforeOp = "before";
    internal const string AfterOp = "after";

    /// <summary>The operation as dispatched: trimmed and case-folded, ahead of every comparison,
    /// so no capability implements its own version of either.</summary>
    private string Folded => Op?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Per-operation coherence, decidable from the record alone: the capability is one this
    /// vocabulary carries, the key is named, and every member the operation does not take is absent.
    /// <b>An argument that cannot change an outcome is refused, not ignored</b> - a value on a
    /// removal and a variant on an add are each a fault rather than a member nothing reads.
    /// </summary>
    /// <param name="index">Where in the caller's own list the operation sits, so a fault names the
    /// operation a caller can find.</param>
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

            // An identity or a variant, never both and never neither: the two address entirely
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
    /// The anchor's own coherence. It reuses this record for its shape, so the two members it
    /// means - the placement in <see cref="Op"/>, the anchored entry in <see cref="Target"/> - are
    /// the only ones it may carry, and its key is the operation's own.
    /// </summary>
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
        // disagree with it; the rest belong to a capability an anchor is not.
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

    private static ArgumentFault? Inert(string at, object? value, string member, bool permitted, string owner) =>
        value is null || permitted
            ? null
            : new ArgumentFault(at, $"\"{at}\" carries a \"{member}\", which means nothing here - it belongs to \"{owner}\".");

    /// <summary>Every field of a mapping entry is a name and a value. A nested object or array has
    /// no place in an entry, and passing one through would be a shape the emitter would have to
    /// invent a rendering for.</summary>
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
    /// <b>The one decode site.</b> The wire projection becomes the substrate's own capability here
    /// and nowhere else, so the write path never sees a caller's shape and no second reading of it
    /// exists to disagree with this one. It is total on a record <see cref="Validate"/> passed.
    /// </summary>
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

    /// <summary>
    /// An entry's payload: the fields in the order they were sent, or a bare value. Order is carried
    /// across because it is the order the emitter writes, and a caller composing an entry chose it.
    ///
    /// A field's text is what a client sent it as. A JSON string is that string; a number or a
    /// boolean is its own literal text, since the parse leaves every frontmatter scalar as text and
    /// a value that is a number in a client's eyes is a string in the note.
    /// </summary>
    private static EntryPayload Payload(JsonNode entry) => entry switch
    {
        JsonObject fields => new EntryPayload.Mapping([.. fields.Select(f => new EntryFieldValue(f.Key, Text(f.Value!)))]),
        _ => new EntryPayload.Scalar(Text(entry)),
    };

    private static string Text(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
}
