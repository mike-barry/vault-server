using System.Diagnostics.CodeAnalysis;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// Represents a well-formed, vault-relative path inside a <see cref="Vault"/>, held as segments and
/// rendered with one separator convention. It carries its vault so an operation
/// taking two paths can require them to agree.
/// </summary>
/// <remarks>
/// Containment is decided textually and touches no filesystem. That keeps the check cheap,
/// applicable to every path, and identical for a path that does not exist yet. This type asserts
/// nothing about existence, about the name grammar, or about whether the path names a file or a
/// folder; each operation checks what it needs where it needs it.
///
/// Containment is not admission. This type says a path is inside its vault, and only
/// <see cref="VaultAccess"/> turns one into a <see cref="ReadPath"/> or a <see cref="WritePath"/>,
/// which are what operations take.
/// </remarks>
public sealed record VaultPath
{
    private VaultPath(Vault vault, string[] segments)
    {
        Vault = vault;
        Segments = segments;
        Value = string.Join('/', segments);
    }

    /// <summary>Gets the vault this path is contained by.</summary>
    public Vault Vault { get; }

    /// <summary>
    /// Gets the normalized, case-preserved segments. Never empty, because the vault root is
    /// represented by <see cref="Access.Vault"/> rather than by a path with no segments.
    /// </summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>
    /// Gets the vault-relative rendering, forward-slash separated and quotable back into a later
    /// call.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets where this path is on disk, composed from the vault root and the segments. Internal,
    /// because an absolute path must not leave this layer; it exists only between a primitive and
    /// the filesystem call it makes.
    /// </summary>
    internal string Absolute => System.IO.Path.Combine([Vault.Root, .. Segments]);

    /// <summary>
    /// Gets the vault-relative rendering of the containing folder, or null when this path sits
    /// directly under the root. The root has no rendering here, so an operation that finds it
    /// missing reports that as an operational failure rather than as a missing parent.
    /// </summary>
    internal string? ParentValue =>
        Segments.Count > 1 ? string.Join('/', Segments.Take(Segments.Count - 1)) : null;

    /// <summary>
    /// Gets whether any segment is dot-prefixed, at any depth.
    /// </summary>
    /// <remarks>
    /// This is a query rather than a refusal at construction because two callers use it
    /// differently: no caller may name such a path, while a whole-vault census asked for dot
    /// entries reports them.
    /// </remarks>
    public bool HasDotSegment => Segments.Any(s => s.StartsWith('.'));

    /// <summary>
    /// Builds a path inside a vault from vault-relative text, or reports which rule the text broke.
    /// Rooted, escaping, empty, and root-denoting text are all malformed. <c>.</c> and <c>..</c>
    /// segments are resolved, and both separator conventions are accepted on input.
    /// </summary>
    /// <remarks>A dot-prefixed segment is not malformed. See <see cref="HasDotSegment"/>.</remarks>
    /// <param name="vault">The vault the path must be inside.</param>
    /// <param name="relativePath">The vault-relative text to build from.</param>
    /// <param name="path">The built path, or null when the text is malformed.</param>
    /// <param name="malformedReason">Which rule the text broke, or null when it is well-formed.</param>
    /// <returns>True when the path was built.</returns>
    public static bool TryCreate(Vault vault, string relativePath, [NotNullWhen(true)] out VaultPath? path, [NotNullWhen(false)] out string? malformedReason)
    {
        try
        {
            path = new VaultPath(vault, ScopePath.Normalize(relativePath));
            malformedReason = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            // ScopePath.Normalize is the one grammar every vault-relative path is read by, so its
            // message states which rule the text broke. The parameter suffix ArgumentException
            // appends is not part of that statement and is trimmed off.
            var suffix = ex.Message.LastIndexOf(" (Parameter", StringComparison.Ordinal);
            path = null;
            malformedReason = suffix < 0 ? ex.Message : ex.Message[..suffix];
            return false;
        }
    }

    /// <summary>
    /// Determines whether two paths name the same entry in the same vault. The comparison is
    /// case-insensitive, matching scope semantics, so two spellings name one file. Move asks the
    /// other question through <see cref="HasSameSpellingAs"/>.
    /// </summary>
    /// <remarks>
    /// Windows: the filesystem is case-insensitive, and this comparison is the root of that premise
    /// for the whole layer - containment, exclusion, scope and identity matching all inherit it from
    /// here, each assuming it rather than checking it, so a port establishes case-insensitive path
    /// equality before it looks at anything else.
    /// </remarks>
    /// <param name="other">The path to compare with.</param>
    /// <returns>True when both paths are in the same vault and name the same entry.</returns>
    public bool Equals(VaultPath? other) =>
        other is not null && Vault == other.Vault && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a hash code consistent with the case-insensitive equality above.</summary>
    /// <returns>A hash code combining the vault and the case-insensitive path.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(Vault, StringComparer.OrdinalIgnoreCase.GetHashCode(Value));

    /// <summary>
    /// Determines whether two paths are spelled identically, comparing ordinally. Move needs this:
    /// a case-only rename names a destination that already exists, and only a case-sensitive
    /// comparison against the resolved source tells that from a collision. It is a named method
    /// rather than the default equality so the exception is visible at its call site.
    /// </summary>
    /// <param name="other">The path to compare with.</param>
    /// <returns>True when both paths are in the same vault and spelled identically.</returns>
    public bool HasSameSpellingAs(VaultPath other) =>
        Vault == other.Vault && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>Returns the vault-relative path.</summary>
    public override string ToString() => Value;
}
