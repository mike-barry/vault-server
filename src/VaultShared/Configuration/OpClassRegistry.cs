namespace VaultShared.Configuration;

/// <summary>
/// The set of valid op-class names, built by <see cref="OpClassRegistryLoader"/> from
/// op-classes.json - a committed, non-secret data file rather than a hardcoded enum, so adding a
/// class is a config edit. It loads first in the cascade, because everything that names an
/// authority validates that name against this registry: <c>GrantStore</c>, <c>ScriptToolLoader</c>,
/// a vault's path policy, and the frontmatter declaration.
/// </summary>
public sealed class OpClassRegistry
{
    private readonly IReadOnlySet<string> _names;

    private OpClassRegistry(IReadOnlySet<string> names) => _names = names;

    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_names;

    /// <summary>An inert registry with no op-classes - every <see cref="IsValid"/> check
    /// fails and <see cref="Canonicalize"/> never matches. op-classes.json stays
    /// all-or-nothing: there is no per-row skip for this file, so a whole-file failure
    /// always produces this empty set rather than a partial one.</summary>
    internal static OpClassRegistry Empty => new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Builds a registry over already-validated names. Internal because the loader is
    /// the only producer: the names have to be non-empty and unique before the set means
    /// anything, and a second producer is a second place that can skip the check.</summary>
    internal static OpClassRegistry Of(IReadOnlySet<string> names) => new(names);

    /// <summary>Returns the registry's canonical-cased name for a case-insensitive match, or null.</summary>
    public string? Canonicalize(string name) => _names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool IsValid(string name) => _names.Contains(name);
}
