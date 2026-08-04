namespace VaultShared.Configuration;

/// <summary>
/// Holds the set of valid op-class names, built by <see cref="OpClassRegistryLoader"/> from
/// op-classes.json. That is a committed, non-secret data file rather than a hardcoded enumeration,
/// so adding a class is a configuration edit.
/// </summary>
/// <remarks>
/// It loads first, because everything that names an authority validates that name against it:
/// <c>GrantStore</c>, <c>ScriptToolLoader</c>, a vault's path policy, and the frontmatter
/// declaration.
/// </remarks>
public sealed class OpClassRegistry
{
    private readonly IReadOnlySet<string> _names;

    private OpClassRegistry(IReadOnlySet<string> names) => _names = names;

    /// <summary>Every valid op-class name, in the registry's own casing.</summary>
    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_names;

    /// <summary>An inert registry holding no op classes, so every <see cref="IsValid"/> check fails
    /// and <see cref="Canonicalize"/> never matches. op-classes.json is all-or-nothing - there is no
    /// per-row skip for this file - so a whole-file failure always produces this rather than a
    /// partial set.</summary>
    internal static OpClassRegistry Empty => new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Builds a registry over already-validated names.</summary>
    /// <param name="names">Non-empty, unique op-class names.</param>
    /// <returns>The registry.</returns>
    /// <remarks>Internal because the loader is the only producer. The names have to be non-empty and
    /// unique before the set means anything, and a second producer would be a second place that can
    /// skip that check.</remarks>
    internal static OpClassRegistry Of(IReadOnlySet<string> names) => new(names);

    /// <summary>Returns the registry's own casing of a name.</summary>
    /// <param name="name">The name to look up, compared case-insensitively.</param>
    /// <returns>The canonical name, or null where the registry does not carry it.</returns>
    public string? Canonicalize(string name) => _names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reports whether the registry carries a name.</summary>
    /// <param name="name">The op-class name.</param>
    /// <returns>True where it is declared.</returns>
    public bool IsValid(string name) => _names.Contains(name);
}
