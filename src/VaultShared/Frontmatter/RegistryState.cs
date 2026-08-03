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
}
