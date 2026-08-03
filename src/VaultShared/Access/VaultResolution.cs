namespace VaultShared.Access;

/// <summary>
/// The answer to <see cref="Vault.Resolve"/>: a usable vault, a name nobody configured, or a
/// configured vault that is not usable right now. The hierarchy is closed by a private
/// constructor, so the three cases are the whole answer and a caller that handles them all has
/// handled everything.
///
/// The two failures are separate cases rather than one carrying a message, because something
/// downstream retries exactly one of them - recovering that distinction by inspecting text breaks
/// on the first reworded message.
/// </summary>
public abstract record VaultResolution
{
    private VaultResolution() { }

    /// <summary>A usable vault. <see cref="Access.Vault.Name"/> is the configuration's spelling.</summary>
    public sealed record Resolved(Vault Vault) : VaultResolution;

    /// <summary>No vault is configured under this name. A caller error, carrying the caller's own spelling.</summary>
    public sealed record Unknown(string Name) : VaultResolution;

    /// <summary>
    /// Configured, and skipped at load time for <paramref name="Reason"/> - today only a root
    /// directory that does not exist. Operational rather than a caller error, and worth retrying.
    /// </summary>
    public sealed record Unavailable(string Name, string Reason) : VaultResolution;
}
