namespace VaultShared.Tools;

/// <summary>
/// Defines the error codes every tool and the shared execute path use. The wire name of a code is
/// the constant's value, not its member name.
/// </summary>
/// <remarks>
/// <para>
/// Codes are stable, because callers branch on them. Never widen an existing code to cover a near
/// fit and never repurpose one; a failure the catalogue does not already cover gets a new constant
/// here.
/// </para>
/// <para>
/// Three rules govern every payload:
/// </para>
/// <list type="number">
/// <item>A payload must be something the caller can lift into a corrected call. <c>suggested</c>,
/// <c>parent</c> and <c>plan</c> qualify. <see cref="OutOfScope"/> and <see cref="ContentChanged"/>
/// carry no payload at all, because their only remedy is elevation or a re-read.</item>
/// <item>A payload names only the coordinates the caller supplied.
/// <see cref="AuthorityRequired"/> repeats the caller's own keys or paths; it never reports what the
/// grant holds, and never the subset that failed, which discloses the same thing in
/// instalments.</item>
/// <item>A payload never answers a question the refusal withheld. No code reveals whether a target
/// the caller may not reach exists.</item>
/// </list>
/// <para>
/// Three codes report operational conditions rather than caller errors, and the identical call may
/// be retried: <see cref="VaultUnavailable"/>, <see cref="LockTimeout"/> and
/// <see cref="SearchFailed"/>. Every other code requires the call itself to change first, and
/// <see cref="ContentChanged"/> requires a re-read before it can. Retryability is a property of the
/// code, so callers decide from the code alone.
/// </para>
/// </remarks>
public static class ToolCodes
{
    // --- Vault resolution -----------------------------------------------------------------

    /// <summary>The call named a vault nobody configured. Caller error.</summary>
    public const string UnknownVault = "unknown_vault";

    /// <summary>The vault is configured but not usable. Operational; retryable.</summary>
    public const string VaultUnavailable = "vault_unavailable";

    // --- Arguments, paths and names -------------------------------------------------------

    /// <summary>The arguments are the wrong shape, break an exclusivity rule, include a parameter
    /// that has no effect, use an unrecognized enumerated value, or supply a block that will not
    /// hold as frontmatter. <c>data: {argument}</c>.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>The path is malformed, escapes the vault, is rooted, is empty, or lacks the markdown
    /// extension. <c>data: {suggested?}</c>.</summary>
    public const string InvalidPath = "invalid_path";

    /// <summary>A new path segment falls outside the name grammar.
    /// <c>data: {suggested?}</c>.</summary>
    public const string InvalidName = "invalid_name";

    /// <summary>The path contains a dot-directory segment, which this tooling does not own. No grant
    /// changes that.</summary>
    public const string PathExcluded = "path_excluded";

    // --- Authority ------------------------------------------------------------------------

    /// <summary>The grant's scope does not reach the target. No payload.</summary>
    public const string OutOfScope = "out_of_scope";

    /// <summary>
    /// The caller lacks an authority the call needs: the op-class floor, a key assertion made
    /// mid-operation, or an artifact class.
    /// </summary>
    /// <remarks>
    /// All three share one code because they share one remedy - elevate, or present a wider grant -
    /// and a caller retrying after elevation does not need to know which check refused.
    ///
    /// The payload repeats the caller's own coordinates for the check that refused and never what
    /// the grant holds: <c>data: {tool, required_op_class}</c> from the floor, <c>{keys}</c> from a
    /// key-level denial, <c>{paths}</c> from an artifact-level one.
    /// </remarks>
    public const string AuthorityRequired = "authority_required";

    // --- Existence and occupancy ----------------------------------------------------------

    /// <summary>The single target the call named does not exist.</summary>
    public const string PathNotFound = "path_not_found";

    /// <summary>The target exists and could not be read. <c>data: {path}</c>.</summary>
    public const string ReadFailed = "read_failed";

    /// <summary>
    /// The target exists and the write failed. <c>data: {path}</c>. This is an operational failure
    /// rather than a caller error: the access layer reports a missing parent, an occupied target and
    /// a refused name separately, and each has its own code above.
    /// </summary>
    public const string WriteFailed = "write_failed";

    /// <summary>The target is already occupied. Used for creation and move destinations.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>The destination's parent directory does not exist. <c>data: {parent}</c>.</summary>
    public const string ParentMissing = "parent_missing";

    /// <summary>The operand is not the kind the call asserted it was.</summary>
    public const string WrongKind = "wrong_kind";

    /// <summary>A folder removal that was not asked to recurse found contents.
    /// <c>data: {plan}</c>.</summary>
    public const string NotEmpty = "not_empty";

    /// <summary>A .git directory lies inside the removal set. <c>data: {paths}</c>.</summary>
    public const string GitDirectoryPresent = "git_directory_present";

    // --- Staleness and body anchors -------------------------------------------------------

    /// <summary>The file changed since the caller took its hash. No payload: returning the current
    /// hash would let a caller retry into content it never read.</summary>
    public const string ContentChanged = "content_changed";

    /// <summary>A substitution anchor matched nothing.</summary>
    public const string BodyMatchNotFound = "body_match_not_found";

    /// <summary>A substitution anchor matched more than once. <c>data: {count}</c>.</summary>
    public const string BodyMatchAmbiguous = "body_match_ambiguous";

    // --- Search ---------------------------------------------------------------------------

    /// <summary>The glob or regular expression is malformed. Caller error.</summary>
    public const string InvalidPattern = "invalid_pattern";

    /// <summary>The search engine failed. <c>data: {detail}</c>. Operational;
    /// retryable.</summary>
    public const string SearchFailed = "search_failed";

    // --- Frontmatter ----------------------------------------------------------------------

    /// <summary>The stored frontmatter block will not parse, or is not a mapping.</summary>
    public const string FrontmatterUnparsable = "frontmatter_unparsable";

    /// <summary>The write would create or destroy a fence, or the note's first line makes the fence
    /// ambiguous.</summary>
    public const string FenceConflict = "fence_conflict";

    /// <summary>The registry is absent or would not load. Frontmatter writes only.</summary>
    public const string FrontmatterUnavailable = "frontmatter_unavailable";

    /// <summary>No declaration recognizes the key.</summary>
    public const string UnknownKey = "unknown_key";

    /// <summary>A value constraint refused the value, or the caller's text carried a control
    /// character. <c>data: {expected}</c>.</summary>
    public const string InvalidValue = "invalid_value";

    /// <summary>A scalar was written to a block key, or a block to a scalar key.</summary>
    public const string ShapeMismatch = "shape_mismatch";

    /// <summary>An identity or anchor resolved to no entry.</summary>
    public const string EntryNotFound = "entry_not_found";

    /// <summary>An identity or anchor resolved to more than one entry.</summary>
    public const string EntryAmbiguous = "entry_ambiguous";

    // --- The execute path's own ------------------------------------------------------------

    /// <summary>The tool needs something this front-end cannot supply. Raised before the tool
    /// runs.</summary>
    public const string ToolUnavailable = "tool_unavailable";

    /// <summary>The mutex lock was not acquired before the timeout elapsed. Raised at the lock; no
    /// tool catches it. <c>data: {timeout}</c>, in seconds. Operational; retryable.</summary>
    public const string LockTimeout = "lock_timeout";

    /// <summary>A mutation over several targets failed partway through.
    /// <c>data: {applied, not_applied, recovery}</c>.</summary>
    public const string PartialMutation = "partial_mutation";

    /// <summary>
    /// No tool is registered under the name the call gave. <c>data: {tool}</c>. This code means an
    /// unknown tool name only; a target that does not exist is <see cref="PathNotFound"/>. Keeping
    /// them apart is what lets a caller branch on either one.
    /// </summary>
    public const string NotFound = "not_found";

    // --- Operational conditions, raised outside the vault tools ------------------------------

    /// <summary>A transient per-principal throttle. <c>data: {limit_per_minute}</c>. Operational;
    /// retryable. Do not code this as a capability or argument failure, which would route a
    /// retryable condition into a branch that never retries.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>A server-side prerequisite the call cannot influence. Neither the caller's authority
    /// nor its arguments would change the outcome.</summary>
    public const string DependencyUnavailable = "dependency_unavailable";
}
