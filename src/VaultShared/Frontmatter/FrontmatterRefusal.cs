namespace VaultShared.Frontmatter;

/// <summary>
/// A hard refusal from the frontmatter surface. Integrity, hygiene, path exclusion and authority
/// denial refuse; schema violations come back as data (<see cref="Violation"/>), because a path that
/// threw on any schema violation would refuse a sweeping fix pass on exactly the notes carrying
/// pre-existing problems.
///
/// One exception type carrying a reason from a closed enumeration and the coordinates that locate
/// the problem - which operation, which key - and nothing else. What each reason is called on the
/// wire belongs to the tool vocabulary; what this type fixes is the set of distinctions that
/// survive to it.
/// </summary>
public sealed class FrontmatterRefusal : Exception
{
    private FrontmatterRefusal(RefusalReason reason, string message, int? operation, IReadOnlyList<string> keys)
        : base(message)
    {
        Reason = reason;
        Operation = operation;
        Keys = keys;
    }

    /// <summary>Which distinction this refusal is.</summary>
    public RefusalReason Reason { get; }

    /// <summary>The index of the operation that owns it, into the request's own operation list, or
    /// null where no single operation does - a stored block that will not parse, a fence conflict,
    /// an excluded path, or an authority denial, which names a key set rather than an
    /// operation.</summary>
    public int? Operation { get; }

    /// <summary>
    /// The keys this refusal locates. One key for a per-operation refusal, the changed key set for
    /// an authority denial, and empty where no key is involved. The set is the call's own
    /// coordinates rather than anything about the caller's authority, so naming it discloses
    /// nothing.
    /// </summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>A refusal that no operation owns.</summary>
    public static FrontmatterRefusal Of(RefusalReason reason, string message) =>
        new(reason, message, null, []);

    /// <summary>A refusal located at one operation and its key.</summary>
    public static FrontmatterRefusal At(RefusalReason reason, int operation, string key, string message) =>
        new(reason, message, operation, [key]);

    /// <summary>A refusal located at one key with no operation behind it - a key of a
    /// caller-supplied block, which is validated whole rather than per operation.</summary>
    public static FrontmatterRefusal OnKey(RefusalReason reason, string key, string message) =>
        new(reason, message, null, [key]);

    /// <summary>
    /// The authority denial. It names the changed key set, never an operation index: no single
    /// operation owns the refusal, because the requirement is the union over what the write turned
    /// out to change.
    /// </summary>
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
/// The distinctions a refusal must preserve, as a closed set. Each is a different remedy: what a
/// caller does next differs between fixing the text it sent, fixing the note, naming a different
/// entry, and asking for elevation.
/// </summary>
public enum RefusalReason
{
    /// <summary>The note's stored block will not parse. Reading tolerates this and treats it as no
    /// block; writing refuses it, because writing into a block nobody could parse means guessing.</summary>
    Unparsable,

    /// <summary>The note's stored block parsed to something other than a mapping.</summary>
    NotAMapping,

    /// <summary>The composed content's frontmatter facet is not what the operations produced - a
    /// write introducing a block no operation authored, or destroying one nobody validated - or the
    /// note's first line makes a fresh block ambiguous.</summary>
    FenceConflict,

    /// <summary>A caller-supplied block that does not hold up as frontmatter: an unclosed fence,
    /// content after the closing fence, text that will not parse, or text that is not a mapping.
    /// Distinct from <see cref="Unparsable"/> by whose text it is - the caller declared this text
    /// is frontmatter, so text that cannot hold as frontmatter is a caller error.</summary>
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
    /// no declared variant or several, a missing or unknown entry field, an exclusive group
    /// satisfied in neither direction, a named variant nothing declares, or an addressing value
    /// that is empty.</summary>
    ValueRefused,

    /// <summary>A flat scalar operation against a field holding a block, or a block-list operation
    /// against one holding a scalar - by the declaration or by the note itself.</summary>
    ShapeMismatch,

    /// <summary>An identity or an anchor resolved to no entry. An anchor that does not resolve fails
    /// rather than falling back to appending.</summary>
    UnresolvedIdentity,

    /// <summary>An identity or an anchor resolved to more than one entry, under the comparison the
    /// variant declares.</summary>
    AmbiguousIdentity,

    /// <summary>A removal would clear entries of a variant that does not declare itself removable
    /// wholesale. Its own reason because the remedy is to name entries one at a time, which is
    /// nothing like the remedy for an identity that matched nothing.</summary>
    WholesaleRemovalRefused,

    /// <summary>The union of what the outcome requires is not held. Names the changed key set.</summary>
    AuthorityDenied,

    /// <summary>The vault's declaration is absent or would not load, so nothing can tell whether a
    /// supplied key is declared. Frontmatter writes refuse; reads, enumeration, search, move and
    /// delete are untouched.</summary>
    RegistryUnavailable,
}
