using System.Diagnostics.CodeAnalysis;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// Proof of containment and nothing else: a well-formed, vault-relative path inside
/// <see cref="Vault"/>, held as segments and rendered with one separator convention on every
/// platform. It carries its vault so an operation taking two paths can require them to agree.
///
/// Containment is decided textually and touches no filesystem, which is what makes the check cheap,
/// applicable to every path, and identical for a path that does not exist yet. Nothing here asserts
/// existence, a conforming name, or whether the path names a file or a folder - each of those is
/// checked by the operation that needs it, where it needs it.
///
/// Containment is not admission. A <see cref="VaultPath"/> says a path is inside its vault; only
/// <see cref="VaultAccess"/> turns one into a <see cref="ReadPath"/> or a <see cref="WritePath"/>,
/// and only those reach an operation.
/// </summary>
public sealed record VaultPath
{
    private VaultPath(Vault vault, string[] segments)
    {
        Vault = vault;
        Segments = segments;
        Value = string.Join('/', segments);
    }

    /// <summary>The vault this path is contained by.</summary>
    public Vault Vault { get; }

    /// <summary>The normalized, case-preserved segments. Never empty: the vault root is not a path.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The vault-relative rendering, forward-slash separated, quotable back into a later call.</summary>
    public string Value { get; }

    /// <summary>
    /// Where this path is on disk, composed from the vault root and the segments. Internal, because
    /// an absolute path never leaves the layer: it exists only between a primitive and the
    /// filesystem call it makes.
    /// </summary>
    internal string Absolute => System.IO.Path.Combine([Vault.Root, .. Segments]);

    /// <summary>
    /// The vault-relative rendering of the containing folder, or null when this path sits directly
    /// under the root. The root is a vault rather than a path, so it has no rendering here, and an
    /// operation that finds it missing reports that as operational rather than as a missing parent.
    /// </summary>
    internal string? ParentValue =>
        Segments.Count > 1 ? string.Join('/', Segments.Take(Segments.Count - 1)) : null;

    /// <summary>
    /// True when any segment is dot-prefixed, at any depth. The operand perimeter and enumeration's
    /// default both turn on this, and they use it differently: no caller may name such a path, while
    /// a census asked for dot entries reports them. That is why the answer is a query here rather
    /// than a refusal at construction.
    /// </summary>
    public bool HasDotSegment => Segments.Any(s => s.StartsWith('.'));

    /// <summary>
    /// Builds a path inside <paramref name="vault"/> from <paramref name="relativePath"/>, or
    /// answers false with the rule the text broke. Rooted, escaping, empty, and root-denoting text
    /// are all malformed; <c>.</c> and <c>..</c> segments resolve, and both separator conventions
    /// are accepted on input.
    ///
    /// A dot-prefixed segment is not malformed - see <see cref="HasDotSegment"/>.
    /// </summary>
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
            // message is the statement of which rule the text broke. The parameter suffix
            // ArgumentException appends is not part of that statement.
            var suffix = ex.Message.LastIndexOf(" (Parameter", StringComparison.Ordinal);
            path = null;
            malformedReason = suffix < 0 ? ex.Message : ex.Message[..suffix];
            return false;
        }
    }

    /// <summary>
    /// Case-insensitive, matching scope semantics: two spellings name one file. Move asks the other
    /// question through <see cref="HasSameSpellingAs"/>.
    /// </summary>
    public bool Equals(VaultPath? other) =>
        other is not null && Vault == other.Vault && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Vault, StringComparer.OrdinalIgnoreCase.GetHashCode(Value));

    /// <summary>
    /// Ordinal comparison, for the one caller that needs it: a case-only rename names a destination
    /// that already exists, and only a case-sensitive comparison against the resolved source tells
    /// that from a collision. Explicit rather than the default, so the carve-out is visible at its
    /// call site.
    /// </summary>
    public bool HasSameSpellingAs(VaultPath other) =>
        Vault == other.Vault && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => Value;
}
