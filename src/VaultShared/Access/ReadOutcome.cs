namespace VaultShared.Access;

/// <summary>
/// The answer to a read: the content, or a classified failure. "Nothing there to read" and "there
/// and something went wrong" are separate cases because every enumerate-then-read pass in the
/// system depends on one definition of the first, and a caller cannot be left to recover it from
/// message text.
///
/// The classification comes from the failure the filesystem raised, never from an existence probe
/// the layer runs first, and it is part of the read rather than a second operation a caller may
/// skip or redo. The hierarchy is closed by a private constructor.
/// </summary>
public abstract record ReadOutcome
{
    private ReadOutcome() { }

    /// <summary>
    /// The note's text, decoded as UTF-8 with any byte-order mark dropped, and the digest of the
    /// bytes it was decoded from.
    /// </summary>
    /// <param name="ContentHash">
    /// SHA-256 of the file's bytes as stored, lowercase hex. It comes from this read rather than
    /// from a second one, so it describes exactly the bytes <paramref name="Text"/> was decoded
    /// from - a later read would hash bytes the returned text may no longer describe. It covers the
    /// whole file whatever a caller then takes out of the text, which is what stops a partial read
    /// from authorizing a write.
    /// </param>
    public sealed record Content(string Text, string ContentHash) : ReadOutcome;

    /// <summary>Nothing at that path, or nothing at a folder along the way.</summary>
    public sealed record NotFound(string Path) : ReadOutcome;

    /// <summary>
    /// There, and the read failed anyway - a permission, a sharing conflict, a device error.
    /// <paramref name="Reason"/> is what the platform said, with locations rendered vault-relative.
    /// </summary>
    public sealed record Failed(string Path, string Reason) : ReadOutcome;
}
