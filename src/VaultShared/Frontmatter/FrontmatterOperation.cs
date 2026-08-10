namespace VaultShared.Frontmatter;

/// <summary>
/// Represents one frontmatter edit, as a closed hierarchy of four cases: set a scalar, remove a
/// field, add an entry to a block list, and remove one. The base constructor is private, so no
/// other assembly can add a fifth case.
///
/// Caller text is trimmed as an operation is constructed, so every case sees the same text and none
/// implements its own trimming. The emptiness test is applied separately, in the write path's
/// validate phase (<see cref="OperationText.IsEmpty"/>), which knows which operation to name in a
/// refusal. Case-insensitivity is a property of the comparisons rather than a rewrite of the
/// caller's text, since lowercasing a key would destroy the spelling a write is supposed to
/// preserve.
/// </summary>
public abstract record FrontmatterOperation
{
    /// <summary>Initializes an operation against a key.</summary>
    /// <param name="key">The frontmatter key being mutated. Trimmed here.</param>
    private FrontmatterOperation(string key) => Key = OperationText.Trim(key);

    /// <summary>Gets the frontmatter key being mutated. Matched against the declaration
    /// case-insensitively and canonicalized to one spelling before any read or write.</summary>
    public string Key { get; }

    /// <summary>Sets a declared scalar key to a value. Refused where the key declares a block list,
    /// or where the note's own key holds one, because a scalar set against a list would replace the
    /// list with a string.</summary>
    /// <param name="Key">The scalar key to set.</param>
    /// <param name="Value">The text to write under it.</param>
    public sealed record SetScalar(string Key, string Value) : FrontmatterOperation(Key)
    {
        /// <summary>Gets the text to write, trimmed.</summary>
        public string Value { get; } = OperationText.Trim(Value);
    }

    /// <summary>
    /// Removes a field. The scalar or block path is chosen from the field's detected shape, never
    /// from the caller declaring which. A block-list field is removable only where every entry
    /// under it belongs to a variant the schema allows clearing wholesale; otherwise the removal is
    /// refused rather than applied in part.
    /// </summary>
    /// <param name="Key">The key to remove.</param>
    public sealed record RemoveField(string Key) : FrontmatterOperation(Key);

    /// <summary>
    /// Adds an entry to a block list. The entry must satisfy exactly one of the variants its schema
    /// declares, and the entry's own fields decide which. The key is created when absent, as is the
    /// block on a note that has none.
    /// </summary>
    /// <param name="Key">The block-list key to add under.</param>
    /// <param name="Entry">The entry's fields and values, or a bare scalar. Never pre-rendered
    /// block text, so a caller's value cannot come out the other side as a key.</param>
    /// <param name="Anchor">Where the entry goes, or null to append. Placement is either explicit
    /// or an append; ordering conventions belong to the tool composing the write.</param>
    public sealed record AddEntry(string Key, EntryPayload Entry, EntryAnchor? Anchor) : FrontmatterOperation(Key);

    /// <summary>Removes an entry from a block list.</summary>
    /// <param name="Key">The block-list key to remove from.</param>
    /// <param name="Target">Which entry or entries to remove: a declared identity, or, where the
    /// schema permits it for that variant, the variant name with every entry of it cleared.</param>
    public sealed record RemoveEntry(string Key, EntryTarget Target) : FrontmatterOperation(Key);
}

/// <summary>
/// Represents what an added entry carries: fields and values, or a bare scalar. Never rendered
/// block text, because each field becomes a tree value that the emitter escapes, so a caller's
/// value can never become structure.
/// </summary>
public abstract record EntryPayload
{
    private EntryPayload() { }

    /// <summary>A bare value, for a schema declaring a scalar variant.</summary>
    /// <param name="Value">The entry's text.</param>
    public sealed record Scalar(string Value) : EntryPayload
    {
        /// <summary>Gets the entry's text, trimmed.</summary>
        public string Value { get; } = OperationText.Trim(Value);
    }

    /// <summary>
    /// A mapping of fields, in the order they are written. An ordered list rather than a dictionary,
    /// because the order is what the emitter writes.
    /// </summary>
    /// <param name="Fields">The fields, in write order.</param>
    public sealed record Mapping(IReadOnlyList<EntryFieldValue> Fields) : EntryPayload
    {
        /// <summary>Gets the fields, in write order.</summary>
        public IReadOnlyList<EntryFieldValue> Fields { get; } = Fields;

        /// <summary>Returns the value written under a field name.</summary>
        /// <param name="name">The field name, matched case-insensitively as field names are
        /// throughout.</param>
        /// <returns>The field's value, or null where the payload carries no such field.</returns>
        public string? Field(string name) =>
            Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}

/// <summary>Represents one field of a mapping payload.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Value">The field's value.</param>
public sealed record EntryFieldValue(string Name, string Value)
{
    /// <summary>Gets the field's name, trimmed.</summary>
    public string Name { get; } = OperationText.Trim(Name);

    /// <summary>Gets the field's value, trimmed.</summary>
    public string Value { get; } = OperationText.Trim(Value);
}

/// <summary>
/// Represents what a removal addresses. Entries are addressed by declared identity, never by index.
/// An index names a different entry the moment an earlier operation shifts the list, while an
/// identity verifies itself.
/// </summary>
public abstract record EntryTarget
{
    private EntryTarget() { }

    /// <summary>One entry, named by the value its variant declares as its identity. Matching more
    /// than one is a refusal; matching none reports no change.</summary>
    /// <param name="Value">The identity value addressing the entry.</param>
    public sealed record Identity(string Value) : EntryTarget
    {
        /// <summary>Gets the identity value, trimmed.</summary>
        public string Value { get; } = OperationText.Trim(Value);
    }

    /// <summary>Every entry of a named variant, with no target value. Refused unless that variant
    /// declares itself removable wholesale.</summary>
    /// <param name="Name">The variant to clear.</param>
    public sealed record Variant(string Name) : EntryTarget
    {
        /// <summary>Gets the variant name, trimmed.</summary>
        public string Name { get; } = OperationText.Trim(Name);
    }
}

/// <summary>
/// Represents where an added entry goes, relative to an entry already in the list. An anchor that
/// resolves to no entry, or to several, is refused rather than falling back to appending, because a
/// silent append puts the entry somewhere the caller did not ask for.
/// </summary>
/// <param name="Placement">Which side of the anchored entry the new one lands on.</param>
/// <param name="Identity">The identity value naming the anchored entry.</param>
public sealed record EntryAnchor(AnchorPlacement Placement, string Identity)
{
    /// <summary>Gets the anchored entry's identity value, trimmed.</summary>
    public string Identity { get; } = OperationText.Trim(Identity);
}

/// <summary>Specifies which side of the anchored entry the new one lands on.</summary>
public enum AnchorPlacement
{
    /// <summary>The new entry goes immediately before the anchored one.</summary>
    Before,

    /// <summary>The new entry goes immediately after the anchored one.</summary>
    After,
}

/// <summary>
/// Handles the text rules every frontmatter operation shares, in one place so no operation
/// implements its own version of any of them.
/// </summary>
public static class OperationText
{
    /// <summary>Returns caller text as the operations hold it. Applied at construction, so every
    /// operation sees the same text.</summary>
    /// <param name="text">The caller's text, which may be null.</param>
    /// <returns>The text trimmed, or the empty string for null.</returns>
    public static string Trim(string? text) => text?.Trim() ?? string.Empty;

    /// <summary>
    /// Returns whether text counts as empty. The additive and subtractive forms of an operation
    /// apply this same test to the same argument, so a value cannot be refused when added and
    /// silently do nothing when removed.
    /// </summary>
    /// <param name="text">The text to test.</param>
    /// <returns>True when the text is null, empty, or whitespace only.</returns>
    public static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Returns whether caller text carries a control character. Every operation carrying caller
    /// text refuses control characters, in entry field values as well as scalar values, because
    /// terminal escape sequences are control characters and the most agent-facing text in the vault
    /// lives in block-list entries. Such text is refused rather than stripped, since silently
    /// rewriting a value would report success while changing what was asked for.
    /// </summary>
    /// <param name="text">The text to test.</param>
    /// <returns>True when the text carries a control character other than CR or LF.</returns>
    /// <remarks>
    /// Line terminators are the one exception, because the emitter handles them. Quotes are not
    /// control characters and are not in scope. A tab is refused with the rest, since it is not a
    /// line terminator and YAML cannot carry one as indentation at all.
    /// </remarks>
    public static bool HasControlCharacter(string text) =>
        text.Any(c => char.IsControl(c) && c is not ('\n' or '\r'));
}
