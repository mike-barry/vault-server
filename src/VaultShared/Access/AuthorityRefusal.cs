namespace VaultShared.Access;

/// <summary>
/// Raised by <see cref="VaultAccess.AssertKey"/> when the caller's grant does not hold a capability
/// key the work turned out to need. It carries the required class and nothing about the grant: what
/// a caller lacks is its own coordinate, what it holds is not.
///
/// It raises rather than answers because the assertion sits mid-operation, where returning a
/// refusal would make surfacing it a convention every call site keeps.
/// </summary>
public sealed class AuthorityRefusal(string opClass)
    : Exception($"This operation requires the \"{opClass}\" op class.")
{
    /// <summary>The op class the operation turned out to require.</summary>
    public string OpClass { get; } = opClass;
}
