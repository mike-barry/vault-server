namespace VaultShared.Access;

/// <summary>
/// The answer to a write-shaped operation - replacing a note's content, creating a note, or
/// creating a folder. One hierarchy across the three, because their refusals are the same refusals;
/// each operation's own documentation says which cases it can produce. The hierarchy is closed by a
/// private constructor.
///
/// Every refusal here comes from the attempt rather than from a prior probe, which is what closes
/// the window in which the editor, another session, or a person changes the target between a check
/// and a write.
/// </summary>
public abstract record WriteOutcome
{
    private WriteOutcome() { }

    /// <summary>The content or the folder landed.</summary>
    /// <param name="ContentHash">
    /// SHA-256 of the bytes this write stored, lowercase hex, taken from the same write rather than
    /// from a read afterwards - so it describes exactly what landed and chains with the hash a read
    /// of the same file answers. Null where the write brought no content into existence, which is
    /// folder creation alone: there is no file to describe.
    /// </param>
    public sealed record Written(string? ContentHash) : WriteOutcome;

    /// <summary>
    /// Nothing there to replace. Replacing is for content that exists; bringing a note into
    /// existence is the creating form's job, and only it checks the name grammar.
    /// </summary>
    public sealed record NotFound(string Path) : WriteOutcome;

    /// <summary>
    /// The creating form's refusal: the target was already there, as the filesystem's answer to an
    /// atomic create. Distinguishable from every other write failure, which is the whole point of
    /// it being its own case.
    /// </summary>
    public sealed record AlreadyExists(string Path) : WriteOutcome;

    /// <summary>
    /// The single segment this call would bring into existence does not satisfy the name grammar
    /// (<see cref="VaultName"/>). Produced by the creating form and by folder creation; replacing
    /// brings no name into existence and so never produces it.
    /// </summary>
    public sealed record InvalidName(string Name) : WriteOutcome;

    /// <summary>
    /// The containing folder is not there, named in vault-relative terms. Nothing creates a parent
    /// on the way past, so materializing a path stays a deliberate call.
    /// </summary>
    public sealed record MissingParent(string Parent) : WriteOutcome;

    /// <summary>
    /// There, and the write failed anyway. <paramref name="Reason"/> is what the platform said,
    /// with locations rendered vault-relative.
    /// </summary>
    public sealed record Failed(string Path, string Reason) : WriteOutcome;
}
