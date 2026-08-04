namespace VaultShared.Access;

/// <summary>
/// Represents the result of <see cref="Vault.Resolve"/>: a usable vault, a name nobody configured,
/// or a configured vault that is not usable right now. The private constructor closes the
/// hierarchy, so a caller that handles every nested record has handled every case.
/// </summary>
/// <remarks>
/// The two failures are separate cases rather than one case carrying a message, because a caller
/// downstream retries exactly one of them and must not have to recover the distinction from message
/// text.
/// </remarks>
public abstract record VaultResolution
{
    private VaultResolution() { }

    /// <summary>The vault is usable.</summary>
    /// <param name="Vault">The vault. Its <see cref="Access.Vault.Name"/> is the configured spelling.</param>
    public sealed record Resolved(Vault Vault) : VaultResolution;

    /// <summary>No vault is configured under this name. This is a caller error.</summary>
    /// <param name="Name">The name as the caller spelled it, since no other spelling exists.</param>
    public sealed record Unknown(string Name) : VaultResolution;

    /// <summary>
    /// The vault is configured and was skipped at load time. This is operational rather than a
    /// caller error, and is worth retrying.
    /// </summary>
    /// <param name="Name">The vault's name, as configuration spells it.</param>
    /// <param name="Reason">Why it was skipped; currently only a root directory that does not exist.</param>
    public sealed record Unavailable(string Name, string Reason) : VaultResolution;
}
