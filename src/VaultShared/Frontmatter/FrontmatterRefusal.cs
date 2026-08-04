namespace VaultShared.Frontmatter;

/// <summary>
/// The exception thrown when the frontmatter surface refuses a write. Integrity, hygiene, path
/// exclusion and authority denial all refuse. Schema violations do not: they come back as data
/// (<see cref="Violation"/>), because throwing on any schema violation would refuse a sweeping fix
/// pass on exactly the notes carrying pre-existing problems.
///
/// The exception carries a reason from a closed enumeration and the coordinates that locate the
/// problem - which operation, which key - and nothing else. What each reason is called on the wire
/// belongs to the tool vocabulary.
/// </summary>
public sealed class FrontmatterRefusal : Exception
{
    /// <summary>Initializes a refusal.</summary>
    /// <param name="reason">Which distinction this refusal is.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="operation">The index of the operation that owns it, or null.</param>
    /// <param name="keys">The keys the refusal locates.</param>
    private FrontmatterRefusal(RefusalReason reason, string message, int? operation, IReadOnlyList<string> keys)
        : base(message)
    {
        Reason = reason;
        Operation = operation;
        Keys = keys;
    }

    /// <summary>Gets which distinction this refusal is.</summary>
    public RefusalReason Reason { get; }

    /// <summary>Gets the index of the operation that owns this refusal, into the request's own
    /// operation list, or null where no single operation does. A stored block that will not parse,
    /// a fence conflict, an excluded path, and an authority denial all have no owning
    /// operation.</summary>
    public int? Operation { get; }

    /// <summary>
    /// Gets the keys this refusal locates: one key for a per-operation refusal, the changed key set
    /// for an authority denial, and empty where no key is involved. The set is the call's own
    /// coordinates and says nothing about what authority the caller holds, so naming it discloses
    /// nothing.
    /// </summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Returns a refusal that no operation owns.</summary>
    /// <param name="reason">Which distinction this refusal is.</param>
    /// <param name="message">The human-readable message.</param>
    /// <returns>The refusal.</returns>
    public static FrontmatterRefusal Of(RefusalReason reason, string message) =>
        new(reason, message, null, []);

    /// <summary>Returns a refusal located at one operation and its key.</summary>
    /// <param name="reason">Which distinction this refusal is.</param>
    /// <param name="operation">The index of the owning operation, into the request's operation list.</param>
    /// <param name="key">The key the operation names.</param>
    /// <param name="message">The human-readable message.</param>
    /// <returns>The refusal.</returns>
    public static FrontmatterRefusal At(RefusalReason reason, int operation, string key, string message) =>
        new(reason, message, operation, [key]);

    /// <summary>Returns a refusal located at one key with no operation behind it, such as a key of
    /// a caller-supplied block, which is validated whole rather than per operation.</summary>
    /// <param name="reason">Which distinction this refusal is.</param>
    /// <param name="key">The key at fault.</param>
    /// <param name="message">The human-readable message.</param>
    /// <returns>The refusal.</returns>
    public static FrontmatterRefusal OnKey(RefusalReason reason, string key, string message) =>
        new(reason, message, null, [key]);

    /// <summary>
    /// Returns an authority denial. It names the changed key set and never an operation index,
    /// because the requirement is the union over what the write turned out to change and so no
    /// single operation owns it.
    /// </summary>
    /// <param name="changedKeys">The keys the write changed.</param>
    /// <returns>The refusal.</returns>
    public static FrontmatterRefusal Denied(IReadOnlyCollection<string> changedKeys) =>
        new(
            RefusalReason.AuthorityDenied,
            $"Writing {Named(changedKeys)} requires authority this caller does not hold.",
            null,
            [.. changedKeys]);

    private static string Named(IReadOnlyCollection<string> keys) =>
        keys.Count == 0 ? "this note" : $"\"{string.Join("\", \"", keys)}\"";
}

/// <summary>
/// Identifies why the frontmatter surface refused a write. The set is closed, and each member
/// implies a different remedy: what a caller does next differs between fixing the text it sent,
/// fixing the note, naming a different entry, and asking for elevation.
/// </summary>
public enum RefusalReason
{
    /// <summary>The note's stored block will not parse. Reading tolerates this and treats it as no
    /// block; writing refuses it, because writing into a block nobody could parse means guessing.</summary>
    Unparsable,

    /// <summary>The note's stored block parsed to something other than a mapping.</summary>
    NotAMapping,

    /// <summary>The composed note's frontmatter is not what the operations produced, either because
    /// the write introduced a block no operation authored or destroyed one nobody validated, or
    /// because the note's first line would make a newly prepended block ambiguous.</summary>
    FenceConflict,

    /// <summary>A caller-supplied block does not hold up as frontmatter: an unclosed fence, content
    /// after the closing fence, text that will not parse, or text that is not a mapping. Distinct
    /// from <see cref="Unparsable"/> by whose text it is - the caller declared this text was
    /// frontmatter, so it is a caller error rather than a defect in the note.</summary>
    SuppliedBlockInvalid,

    /// <summary>The note is under a dot segment. Distinct from an authorization denial: the tooling
    /// does not own the file, and no grant changes that.</summary>
    PathExcluded,

    /// <summary>Caller text carried a control character. Refused rather than stripped.</summary>
    ControlCharacter,

    /// <summary>No declaration recognizes the key. The allowlist refuses it before its shape
    /// matters at all.</summary>
    UnknownKey,

    /// <summary>A value the declaration will not take: a failed value constraint, an entry matching
    /// no declared variant or several of them, a missing or unknown entry field, an exclusive group
    /// satisfied by neither zero nor exactly one field, a variant name nothing declares, or an
    /// addressing value that is empty.</summary>
    ValueRefused,

    /// <summary>A scalar operation against a key holding a block, or a block-list operation against
    /// a key holding a scalar, whether that shape came from the declaration or from the note
    /// itself.</summary>
    ShapeMismatch,

    /// <summary>An identity or an anchor resolved to no entry. An anchor that does not resolve fails
    /// rather than falling back to appending.</summary>
    UnresolvedIdentity,

    /// <summary>An identity or an anchor resolved to more than one entry, under the comparison the
    /// variant declares.</summary>
    AmbiguousIdentity,

    /// <summary>A removal would clear entries of a variant that does not declare itself removable
    /// wholesale. It is its own reason because the remedy is to name entries one at a time, which
    /// is nothing like the remedy for an identity that matched nothing.</summary>
    WholesaleRemovalRefused,

    /// <summary>The caller does not hold the union of op classes the outcome required. The refusal
    /// names the changed key set.</summary>
    AuthorityDenied,

    /// <summary>The vault's declaration is absent or would not load, so nothing can tell whether a
    /// supplied key is declared. Frontmatter writes refuse; reads, enumeration, search, move and
    /// delete are unaffected.</summary>
    RegistryUnavailable,
}
