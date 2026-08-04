namespace VaultShared.Auth;

/// <summary>
/// Configures the localhost auth policy <see cref="GrantStore.ResolveLocalhostGrant"/> applies when a
/// presented bearer token authenticates to no grant row, minted grant, or active elevation binding.
/// </summary>
/// <remarks>
/// <para>
/// The server is localhost-only, so this fallback never errors. A token that resolves to nothing
/// degrades to a harmless floor rather than returning 401.
/// </para>
/// <para>
/// There are two things to configure. The first is which token binds to the default grant: with
/// <see cref="ExpectedLocalhostToken"/> unset, the well-known, non-secret
/// <see cref="SelectorToken"/> selects the default binding; with it set, that value becomes the only
/// token that selects the default. Setting it is a choice of which token is the selector, not a
/// switch that rejects unknown tokens - anything else still floors to minimum access.
/// </para>
/// <para>
/// The second is what the default binding is. <see cref="DefaultGrantId"/> names a token-less named
/// grant, such as <c>admin</c> on a development machine, that the selector token binds to. Null or
/// empty means no default is in effect, and the selector floors to minimum access as well.
/// </para>
/// </remarks>
/// <param name="SelectorToken">
/// The well-known, non-secret token that identifies an interactive development session. It is not a
/// guarded credential; localhost auth protects nothing the filesystem does not. Defaults to
/// <see cref="DefaultSelectorToken"/>.
/// </param>
/// <param name="ExpectedLocalhostToken">
/// When set, replaces <see cref="SelectorToken"/> as the token that selects the default binding.
/// When null, the well-known selector applies. It never causes a rejection: a non-matching token
/// still floors to minimum access.
/// </param>
/// <param name="DefaultGrantId">
/// The id of the token-less named grant the selector token binds to, such as <c>admin</c> on a
/// development machine. Null or empty means the selector floors to minimum access like any other
/// unrecognized token.
/// </param>
public sealed record LocalhostAuthOptions(
    string SelectorToken,
    string? ExpectedLocalhostToken = null,
    string? DefaultGrantId = null)
{
    /// <summary>
    /// The built-in selector token used when none is configured. It is a non-secret, well-known
    /// string, and it selects the default binding only when a <see cref="DefaultGrantId"/> is also
    /// configured, so on its own it does nothing.
    /// </summary>
    public const string DefaultSelectorToken = "vault-mcp-localhost-selector";

    /// <summary>The default options: the built-in selector, no expected token, and no default
    /// binding, so every unrecognized token including the selector floors to minimum access. This is
    /// the posture a bare <see cref="GrantStore"/> starts in.</summary>
    public static LocalhostAuthOptions Default { get; } = new(DefaultSelectorToken);

    /// <summary>The token that selects the default binding: <see cref="ExpectedLocalhostToken"/>
    /// where it is set, otherwise <see cref="SelectorToken"/>.</summary>
    public string EffectiveSelectorToken =>
        string.IsNullOrEmpty(ExpectedLocalhostToken) ? SelectorToken : ExpectedLocalhostToken;
}
