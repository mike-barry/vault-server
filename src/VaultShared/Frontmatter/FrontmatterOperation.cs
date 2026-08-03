namespace VaultShared.Frontmatter;

/// <summary>
/// The four capabilities the substrate makes expressible - set a scalar, remove a field, add an
/// entry to a block list, remove one - as a closed hierarchy. Nothing above may add a fifth: the
/// nested cases are the whole set, and the base constructor is private so no assembly can derive
/// another.
///
/// An operation names three separate things and keeps them apart: the declared key it mutates, the
/// capability being applied, and - for a block-list entry - the discriminating field whose presence
/// says which variant the entry is. Only the first is on this type; the third is carried by the
/// payload, because the payload is what states its own variant.
///
/// <b>Trimming happens here, where an operation is constructed.</b> Every capability therefore sees
/// the same text, and no capability implements its own version of it. The other two decodings sit
/// beside it rather than on the record: the emptiness test runs in the write path's validate phase
/// (<see cref="OperationText.IsEmpty"/>), which is ahead of dispatch and knows which operation to
/// name in a refusal, and case-folding is a property of every comparison here rather than a rewrite
/// of the caller's text - lowercasing a key would destroy the spelling a write is supposed to
/// preserve.
/// </summary>
public abstract record FrontmatterOperation
{
    private FrontmatterOperation(string key) => Key = OperationText.Trim(key);

    /// <summary>The frontmatter key being mutated. Matched against the declaration
    /// case-insensitively and canonicalized to one spelling before any read or write.</summary>
    public string Key { get; }

    /// <summary>Set a declared scalar key to a value. Refused where the key declares a block list,
    /// or where the note's own field holds one - a flat scalar operation against a list would
    /// replace the list with a string.</summary>
    public sealed record SetScalar(string Key, string Value) : FrontmatterOperation(Key)
    {
        public string Value { get; } = OperationText.Trim(Value);
    }

    /// <summary>
    /// Remove a field. The scalar or block path is chosen from the field's detected shape, never
    /// from the caller declaring which. A block-list field is removable only where every entry
    /// under it belongs to a variant the schema allows clearing wholesale; otherwise the removal is
    /// refused rather than applied in part.
    /// </summary>
    public sealed record RemoveField(string Key) : FrontmatterOperation(Key);

    /// <summary>
    /// Add an entry to a block list. The entry must satisfy exactly one of the variants its schema
    /// declares, and the entry's own fields decide which. The key is created when absent, and the
    /// block on a note that has none.
    /// </summary>
    /// <param name="Entry">The entry's fields and values, or a bare scalar - never pre-rendered
    /// block text, so a caller's value cannot come out the other side as a key.</param>
    /// <param name="Anchor">Where it goes. Null appends: placement is explicit or it is an append,
    /// and ordering conventions belong to the tool composing the write.</param>
    public sealed record AddEntry(string Key, EntryPayload Entry, EntryAnchor? Anchor) : FrontmatterOperation(Key);

    /// <summary>Remove an entry from a block list, addressed by its declared identity or - where
    /// the schema permits it for that variant - by naming the variant and clearing every entry of
    /// it.</summary>
    public sealed record RemoveEntry(string Key, EntryTarget Target) : FrontmatterOperation(Key);
}

/// <summary>
/// What an added entry carries. Fields and values, or a bare scalar, and never rendered block text:
/// each field becomes a tree value that the emitter escapes, so a caller's value can never become
/// structure.
/// </summary>
public abstract record EntryPayload
{
    private EntryPayload() { }

    /// <summary>A bare value, for a schema declaring a scalar variant.</summary>
    public sealed record Scalar(string Value) : EntryPayload
    {
        public string Value { get; } = OperationText.Trim(Value);
    }

    /// <summary>
    /// A mapping of fields, in the order they are written. An ordered list rather than a dictionary
    /// because the order is what the emitter writes, and a dictionary would decide it elsewhere.
    /// </summary>
    public sealed record Mapping(IReadOnlyList<EntryFieldValue> Fields) : EntryPayload
    {
        public IReadOnlyList<EntryFieldValue> Fields { get; } = Fields;

        /// <summary>The value written under <paramref name="name"/>, matched case-insensitively as
        /// field names are throughout, or null where the payload carries no such field.</summary>
        public string? Field(string name) =>
            Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}

/// <summary>One field of a mapping payload.</summary>
public sealed record EntryFieldValue(string Name, string Value)
{
    public string Name { get; } = OperationText.Trim(Name);

    public string Value { get; } = OperationText.Trim(Value);
}

/// <summary>
/// What a removal addresses. Entries are addressed by declared identity, never by index - an index
/// names a different entry the moment an earlier operation shifts the list, and an identity
/// self-verifies.
/// </summary>
public abstract record EntryTarget
{
    private EntryTarget() { }

    /// <summary>One entry, by the value its variant declares as its identity. Matching more than
    /// one is a refusal; matching none reports no change.</summary>
    public sealed record Identity(string Value) : EntryTarget
    {
        public string Value { get; } = OperationText.Trim(Value);
    }

    /// <summary>Every entry of a named variant, with no target value. Refused unless that variant
    /// declares itself removable wholesale - entries a human authored are dropped one at a time,
    /// entries the tooling owns and rewrites are usefully cleared in one go.</summary>
    public sealed record Variant(string Name) : EntryTarget
    {
        public string Name { get; } = OperationText.Trim(Name);
    }
}

/// <summary>
/// Where an added entry goes, relative to an entry already in the list. An anchor that does not
/// resolve - naming no entry, or several - is refused rather than falling back to appending, since
/// a silent append puts the entry somewhere the caller did not ask for.
/// </summary>
public sealed record EntryAnchor(AnchorPlacement Placement, string Identity)
{
    public string Identity { get; } = OperationText.Trim(Identity);
}

/// <summary>Which side of the anchored entry the new one lands on.</summary>
public enum AnchorPlacement
{
    Before,
    After,
}

/// <summary>
/// The decodings every operation shares, in one place so no capability implements its own version
/// of any of them.
/// </summary>
public static class OperationText
{
    /// <summary>Caller text as the operations hold it. Applied at construction, so every capability
    /// sees the same text.</summary>
    public static string Trim(string? text) => text?.Trim() ?? string.Empty;

    /// <summary>
    /// The one emptiness test: null, empty, or whitespace-only. The additive and subtractive forms
    /// of an operation apply this same test to the same argument - under two tests the same value
    /// is refused when added and silently does nothing when removed.
    /// </summary>
    public static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Whether caller text carries a control character. Every operation carrying caller text refuses
    /// them, entry field values as well as scalar values - terminal escapes are control characters,
    /// and the most agent-facing text in the vault lives in block-list entries. It refuses rather
    /// than strips: silently rewriting a value reports success while changing what was asked for.
    ///
    /// Line terminators are the one exception, because the emitter handles them, which keeps hygiene
    /// a policy about content rather than an artifact of a format. Quotes are not control characters
    /// and were never in scope. A tab is refused with the rest: it is not a terminator, and YAML
    /// cannot carry one as indentation at all.
    /// </summary>
    public static bool HasControlCharacter(string text) =>
        text.Any(c => char.IsControl(c) && c is not ('\n' or '\r'));
}
