namespace VaultShared.Access;

/// <summary>
/// Represents the exception <see cref="VaultAccess.AssertKey"/> throws when the caller's grant does
/// not hold a capability key the work turned out to need.
/// </summary>
/// <remarks>
/// It carries the required op class and nothing about the grant. What the caller asked for may be
/// echoed back; what the grant holds must not be disclosed.
/// </remarks>
/// <param name="opClass">The op class the operation requires.</param>
public sealed class AuthorityRefusal(string opClass)
    : Exception($"This operation requires the \"{opClass}\" op class.")
{
    /// <summary>Gets the op class the operation requires.</summary>
    public string OpClass { get; } = opClass;
}
