namespace VaultShared.Frontmatter;

/// <summary>
/// What one vault's declaration load answered. Three cases, and null is not one of them: an empty
/// registry standing in for a failed load reads as a vocabulary that recognizes nothing, which
/// turns fail-closed into a vault with nothing protected.
///
/// <see cref="Absent"/> and <see cref="Unloadable"/> refuse identically and report differently,
/// because an operator's next move differs - one is a file to put there, the other a file to fix.
/// What a failed load refuses is exactly the frontmatter writes; reads, enumeration, search, move
/// and delete are untouched, and what prices those is path policy.
/// </summary>
public abstract record RegistryState
{
    private RegistryState() { }

    /// <summary>The declaration loaded and passed every load-time check.</summary>
    public sealed record Loaded(FrontmatterRegistry Registry) : RegistryState;

    /// <summary>No declaration to read. <paramref name="Path"/> is where one was looked for, or
    /// empty when the vault's configuration names none at all.</summary>
    public sealed record Absent(string Path) : RegistryState;

    /// <summary>A declaration that is there and does not hold up. <paramref name="Why"/> is the
    /// first load-time check it failed, phrased for the operator who has to fix the file.</summary>
    public sealed record Unloadable(string Path, string Why) : RegistryState;

    /// <summary>
    /// The registry a frontmatter write runs against, or the refusal that there is none. <b>This is
    /// the fail-closed seam</b>: <see cref="FrontmatterWrite"/> takes a loaded registry, and this is
    /// the only way a caller holding a state gets one, so a write without a declaration cannot be
    /// made quietly - the alternative would be every write site remembering to match on
    /// <see cref="Loaded"/> first, which is a convention rather than a guarantee.
    ///
    /// It refuses in the same vocabulary as every other frontmatter refusal, because refusing at the
    /// write path is where all of them already live. Reads, enumeration, search, move and delete
    /// never reach here and are untouched, and advertisement is unaffected: the vocabulary
    /// projection reads a state rather than requiring one, so <c>edit</c> stays listed with its
    /// variant branches simply absent.
    /// </summary>
    /// <exception cref="FrontmatterRefusal">The declaration is absent or would not load.</exception>
    public FrontmatterRegistry Require() => this switch
    {
        Loaded loaded => loaded.Registry,
        Absent { Path.Length: 0 } => throw Unavailable("This vault names no frontmatter declaration."),
        Absent absent => throw Unavailable($"This vault's frontmatter declaration is not there ({absent.Path})."),
        Unloadable unloadable => throw Unavailable(
            $"This vault's frontmatter declaration will not load ({unloadable.Path}): {unloadable.Why}"),
        _ => throw Unavailable("This vault has no usable frontmatter declaration."),
    };

    private static FrontmatterRefusal Unavailable(string why) =>
        FrontmatterRefusal.Of(
            RefusalReason.RegistryUnavailable,
            $"{why} Frontmatter writes refuse until it loads; everything else is unaffected.");
}
