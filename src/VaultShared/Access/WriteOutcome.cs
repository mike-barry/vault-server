namespace VaultShared.Access;

/// <summary>
/// Represents the result of a write: replacing a note's content, creating a note, or creating a
/// folder. One hierarchy covers all three because their refusals are the same; each operation
/// documents which cases it can produce.
/// </summary>
/// <remarks>
/// Every refusal here comes from the attempt itself rather than from a check made first. That
/// closes the window in which the editor, another session, or a person changes the target between
/// the check and the write.
/// </remarks>
public abstract record WriteOutcome
{
    private WriteOutcome() { }

    /// <summary>The content or the folder landed.</summary>
    /// <param name="ContentHash">
    /// SHA-256 of the bytes this write stored, lowercase hex. It is computed from the write rather
    /// than from a read afterwards, so it describes exactly what landed and matches the hash a read
    /// of the same file returns. Null when the write stored no content, which is folder creation
    /// alone.
    /// </param>
    public sealed record Written(string? ContentHash) : WriteOutcome;

    /// <summary>
    /// Nothing was there to replace. Replacing requires content that already exists; creating a
    /// note is a separate operation, and only that one checks the name grammar.
    /// </summary>
    /// <param name="Path">The target of the write, vault-relative.</param>
    public sealed record NotFound(string Path) : WriteOutcome;

    /// <summary>
    /// The target already exists, as reported by an atomic create. Only the creating operations
    /// produce this. It is its own case so a caller can tell it from every other write failure.
    /// </summary>
    /// <param name="Path">The target of the write, vault-relative.</param>
    public sealed record AlreadyExists(string Path) : WriteOutcome;

    /// <summary>
    /// The single segment this call would create does not satisfy the name grammar
    /// (<see cref="VaultName"/>). Only the creating operations produce this; replacing creates no
    /// name.
    /// </summary>
    /// <param name="Name">The final segment, as the caller spelled it.</param>
    public sealed record InvalidName(string Name) : WriteOutcome;

    /// <summary>
    /// The containing folder does not exist. No operation creates a parent on the way past, so
    /// materializing a path stays a deliberate call.
    /// </summary>
    /// <param name="Parent">The containing folder, vault-relative.</param>
    public sealed record MissingParent(string Parent) : WriteOutcome;

    /// <summary>The target is there and the write failed anyway.</summary>
    /// <param name="Path">The target of the write, vault-relative.</param>
    /// <param name="Reason">
    /// What the platform reported, with any locations in it rendered vault-relative.
    /// </param>
    public sealed record Failed(string Path, string Reason) : WriteOutcome;
}
