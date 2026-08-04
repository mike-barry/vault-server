namespace VaultShared.Frontmatter;

/// <summary>
/// Represents the result of loading one vault's frontmatter declaration, as three cases. Null is
/// not one of them, and neither is an empty registry: an empty registry standing in for a failed
/// load would read as a vocabulary that recognizes nothing, turning fail-closed into a vault with
/// nothing protected.
///
/// <see cref="Absent"/> and <see cref="Unloadable"/> refuse identically and report differently,
/// because the operator's next move differs: one is a file to put there, the other a file to fix.
/// A failed load refuses frontmatter writes only. Reads, enumeration, search, move and delete are
/// unaffected, and path policy is what gates those.
/// </summary>
public abstract record RegistryState
{
    private RegistryState() { }

    /// <summary>The declaration loaded and passed every load-time check.</summary>
    /// <param name="Registry">The loaded declaration.</param>
    public sealed record Loaded(FrontmatterRegistry Registry) : RegistryState;

    /// <summary>There is no declaration to read.</summary>
    /// <param name="Path">Where one was looked for, or empty when the vault's configuration names
    /// none at all.</param>
    public sealed record Absent(string Path) : RegistryState;

    /// <summary>The declaration is there and does not hold up.</summary>
    /// <param name="Path">Where the file is.</param>
    /// <param name="Why">The first load-time check it failed, phrased for the operator who has to
    /// fix the file.</param>
    public sealed record Unloadable(string Path, string Why) : RegistryState;

    /// <summary>
    /// Returns the registry a frontmatter write runs against, or throws if there is none. This is
    /// the fail-closed seam: <see cref="FrontmatterWrite"/> takes a loaded registry, and this is
    /// the only way a caller holding a state can produce one, so a write cannot quietly proceed
    /// without a declaration. The alternative - every write site remembering to match on
    /// <see cref="Loaded"/> first - would be a convention rather than a guarantee.
    ///
    /// The throw uses the same refusal vocabulary as every other frontmatter refusal. Reads,
    /// enumeration, search, move and delete never reach here. Advertisement is also unaffected: the
    /// vocabulary projection reads a state rather than requiring one, so <c>edit</c> stays listed
    /// with its variant branches simply absent.
    /// </summary>
    /// <returns>The loaded declaration.</returns>
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
