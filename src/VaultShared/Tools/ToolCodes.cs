namespace VaultShared.Tools;

/// <summary>
/// The one error catalogue every tool and the shared execute path draw from. Codes are stable: a
/// caller branches on what one means, so an existing code is never widened to cover a near fit and
/// never repurposed. The set extends additively - a failure it does not carry earns a new constant
/// here, under the payload rules below.
///
/// <b>Three rules govern every payload.</b>
/// <list type="number">
/// <item><b>A payload is liftable into a corrected call.</b> <c>suggested</c>, <c>parent</c> and
/// <c>plan</c> are; <see cref="OutOfScope"/> and <see cref="ContentChanged"/> therefore carry
/// nothing, because their only remedy is elevation or a re-read.</item>
/// <item><b>A payload names the caller's own coordinates only.</b> <see cref="AuthorityRequired"/>
/// names the keys or paths the caller supplied - never what the grant holds, and never the subset
/// that failed, which is the same disclosure paid in instalments.</item>
/// <item><b>A payload never answers a question the refusal withheld.</b> No code discloses whether
/// a target the caller may not reach exists.</item>
/// </list>
///
/// <b>Three codes are operational rather than caller errors, and the identical call may be
/// retried:</b> <see cref="VaultUnavailable"/>, <see cref="LockTimeout"/> and
/// <see cref="SearchFailed"/>. Every other code needs the call itself to change first, and
/// <see cref="ContentChanged"/> needs a re-read before it can. Retryability is a property of the
/// code, which is why a caller branches on that rather than on what came back with it.
///
/// Wire names are the constants' values, never the member names.
/// </summary>
public static class ToolCodes
{
    // --- Vault resolution -----------------------------------------------------------------

    /// <summary>A vault nobody configured. Caller error.</summary>
    public const string UnknownVault = "unknown_vault";

    /// <summary>Configured but not usable. Operational; retryable.</summary>
    public const string VaultUnavailable = "vault_unavailable";

    // --- Arguments, paths and names -------------------------------------------------------

    /// <summary>Shape, exclusivity, an inert parameter, an unrecognized enumerated value, or a
    /// supplied block that will not hold as frontmatter. <c>data: {argument}</c>.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>Malformed, escaping, rooted, empty, or missing the markdown extension.
    /// <c>data: {suggested?}</c>.</summary>
    public const string InvalidPath = "invalid_path";

    /// <summary>A new segment outside the name grammar. <c>data: {suggested?}</c>.</summary>
    public const string InvalidName = "invalid_name";

    /// <summary>A dot-directory segment. The tooling does not own it, and no grant changes
    /// that.</summary>
    public const string PathExcluded = "path_excluded";

    // --- Authority ------------------------------------------------------------------------

    /// <summary>The grant does not reach it. No payload.</summary>
    public const string OutOfScope = "out_of_scope";

    /// <summary>
    /// An authority the caller lacks: the op-class floor, a mid-operation key assertion, or an
    /// artifact class. One code, because the remedy is one - elevate, or present a wider grant -
    /// and a caller retrying after elevation does not have to know which check refused.
    ///
    /// The payload names the caller's own coordinates for the check that refused, never what the
    /// grant holds: <c>data: {tool, required_op_class}</c> from the floor, <c>{keys}</c> from a
    /// key-level denial, <c>{paths}</c> from an artifact-level one.
    /// </summary>
    public const string AuthorityRequired = "authority_required";

    // --- Existence and occupancy ----------------------------------------------------------

    /// <summary>The sole target is not there.</summary>
    public const string PathNotFound = "path_not_found";

    /// <summary>There, and the read failed. <c>data: {path}</c>.</summary>
    public const string ReadFailed = "read_failed";

    /// <summary>
    /// There, and the write failed. <c>data: {path}</c>. Operational rather than a caller error:
    /// the layer's write outcomes distinguish this case from a missing parent, an occupied target
    /// and a refused name, each of which has a code of its own above.
    /// </summary>
    public const string WriteFailed = "write_failed";

    /// <summary>The target is occupied. Creation and move destinations.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>The destination's parent does not exist. <c>data: {parent}</c>.</summary>
    public const string ParentMissing = "parent_missing";

    /// <summary>The operand is not the kind asserted.</summary>
    public const string WrongKind = "wrong_kind";

    /// <summary>A plain folder removal met contents. <c>data: {plan}</c>.</summary>
    public const string NotEmpty = "not_empty";

    /// <summary>A .git directory is inside the removal set. <c>data: {paths}</c>.</summary>
    public const string GitDirectoryPresent = "git_directory_present";

    // --- Staleness and body anchors -------------------------------------------------------

    /// <summary>The file changed since the hash was taken. No payload - returning the current hash
    /// would let a caller retry into content it never read.</summary>
    public const string ContentChanged = "content_changed";

    /// <summary>A substitution anchor matched nothing.</summary>
    public const string BodyMatchNotFound = "body_match_not_found";

    /// <summary>A substitution anchor matched more than once. <c>data: {count}</c>.</summary>
    public const string BodyMatchAmbiguous = "body_match_ambiguous";

    // --- Search ---------------------------------------------------------------------------

    /// <summary>A malformed glob or regular expression. Caller error.</summary>
    public const string InvalidPattern = "invalid_pattern";

    /// <summary>The engine failed. <c>data: {detail}</c>. Operational; retryable.</summary>
    public const string SearchFailed = "search_failed";

    // --- Frontmatter ----------------------------------------------------------------------

    /// <summary>The stored block will not parse, or is not a mapping.</summary>
    public const string FrontmatterUnparsable = "frontmatter_unparsable";

    /// <summary>A write would create or destroy a fence, or the note's first line makes one
    /// ambiguous.</summary>
    public const string FenceConflict = "fence_conflict";

    /// <summary>The registry is absent or would not load. Frontmatter writes only.</summary>
    public const string FrontmatterUnavailable = "frontmatter_unavailable";

    /// <summary>No declaration recognizes the key.</summary>
    public const string UnknownKey = "unknown_key";

    /// <summary>A value constraint refused, or caller text carried a control character.
    /// <c>data: {expected}</c>.</summary>
    public const string InvalidValue = "invalid_value";

    /// <summary>Scalar written to a block key, or the reverse.</summary>
    public const string ShapeMismatch = "shape_mismatch";

    /// <summary>An identity or anchor resolved to nothing.</summary>
    public const string EntryNotFound = "entry_not_found";

    /// <summary>An identity or anchor resolved to more than one.</summary>
    public const string EntryAmbiguous = "entry_ambiguous";

    // --- The execute path's own ------------------------------------------------------------

    /// <summary>The tool needs what this front-end cannot supply. Raised before it runs.</summary>
    public const string ToolUnavailable = "tool_unavailable";

    /// <summary>The mutex lock was not acquired before the timeout elapsed. Raised at the lock; no
    /// tool catches it. <c>data: {timeout}</c>, in seconds. Operational; retryable.</summary>
    public const string LockTimeout = "lock_timeout";

    /// <summary>A multi-target mutation failed partway.
    /// <c>data: {applied, not_applied, recovery}</c>.</summary>
    public const string PartialMutation = "partial_mutation";

    /// <summary>
    /// No tool is registered under the name the call gave. Spent on an unknown <em>tool</em> name
    /// rather than a missing path, which is why the missing-target code above is
    /// <see cref="PathNotFound"/>: a caller branching on one code for two unrelated conditions is
    /// the collapse the vocabulary exists to prevent. <c>data: {tool}</c>.
    /// </summary>
    public const string NotFound = "not_found";

    // --- Operational conditions, raised outside the vault tools ------------------------------

    /// <summary>A transient per-principal throttle. <c>data: {limit_per_minute}</c>. Operational;
    /// retryable - coding it as a capability or argument failure would route a retryable condition
    /// into a branch that never retries.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>A server-side prerequisite the call cannot influence. Neither the caller's authority
    /// nor its arguments would change the outcome.</summary>
    public const string DependencyUnavailable = "dependency_unavailable";
}
