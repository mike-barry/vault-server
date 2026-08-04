namespace VaultShared.Access;

/// <summary>
/// Represents the result of reading a note: its content, or a classified failure. The private
/// constructor closes the hierarchy, so a caller that handles every nested record has handled every
/// case.
/// </summary>
/// <remarks>
/// "Nothing there" and "there, and the read failed" are separate cases so a caller does not have to
/// recover the difference from message text. The classification comes from the exception the
/// filesystem raised, never from an existence check made first.
/// </remarks>
public abstract record ReadOutcome
{
    private ReadOutcome() { }

    /// <summary>The note was read.</summary>
    /// <param name="Text">The note's text, decoded as UTF-8 with any byte-order mark dropped.</param>
    /// <param name="ContentHash">
    /// SHA-256 of the file's bytes as stored, lowercase hex. It is computed from this read, so it
    /// describes exactly the bytes <paramref name="Text"/> was decoded from; a second read could
    /// hash different bytes. It covers the whole file whatever the caller then takes out of the
    /// text, which is what stops a partial read from authorizing a write.
    /// </param>
    public sealed record Content(string Text, string ContentHash) : ReadOutcome;

    /// <summary>Nothing is at that path, or a folder along the way is missing.</summary>
    /// <param name="Path">The note that was read, vault-relative.</param>
    public sealed record NotFound(string Path) : ReadOutcome;

    /// <summary>
    /// The note is there and the read failed anyway - a permission problem, a sharing conflict, a
    /// device error.
    /// </summary>
    /// <param name="Path">The note that was read, vault-relative.</param>
    /// <param name="Reason">
    /// What the platform reported, with any locations in it rendered vault-relative.
    /// </param>
    public sealed record Failed(string Path, string Reason) : ReadOutcome;
}
