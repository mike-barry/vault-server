namespace VaultMcpServer.Auth;

/// <summary>
/// The localhost auth policy <see cref="GrantStore.ResolveLocalhostGrant"/> applies when a
/// presented bearer token does not authenticate to any real grant row, minted grant, or active
/// elevation binding. The server is localhost-only today (Kestrel binds <c>localhost:5111</c>),
/// so this fallback never errors: a token that resolves to nothing degrades to a harmless floor
/// rather than a 401.
///
/// Two configuration surfaces:
/// <list type="bullet">
/// <item><b>Which token binds to the default grant.</b> In the permissive model
/// (<see cref="ExpectedLocalhostToken"/> unset) the well-known, non-secret
/// <see cref="SelectorToken"/> selects the default binding. In strict mode
/// (<see cref="ExpectedLocalhostToken"/> set) that configured value is the only token that
/// selects the default; everything else still floors to minimum access - it never errors, so
/// strict mode is a "which token is the selector" knob, not a "reject unknown tokens" one.</item>
/// <item><b>What the default binding is.</b> <see cref="DefaultGrantId"/> names a token-less
/// named grant (e.g. <c>admin</c> on a dev machine) the selector token binds to. When null or
/// empty, the selector binds to the minimum-access floor too - i.e. no dev default is in effect.</item>
/// </list>
/// </summary>
/// <param name="SelectorToken">
/// The well-known, non-secret selector token that identifies an interactive dev session in the
/// permissive model. Committed in both repos' client registration (a later batch); not a guarded
/// credential - localhost auth protects nothing the filesystem doesn't. Defaults to
/// <see cref="DefaultSelectorToken"/>.
/// </param>
/// <param name="ExpectedLocalhostToken">
/// Optional strictness knob. When set, tokens must match this exact value to select the default
/// binding (it replaces <see cref="SelectorToken"/> as the effective selector); when null, the
/// permissive selector model applies. Never causes a rejection - a non-matching token still
/// floors to minimum access.
/// </param>
/// <param name="DefaultGrantId">
/// The id of the token-less named grant the selector token binds to (the dev default, e.g.
/// <c>admin</c>). Null or empty means no dev default: the selector floors to minimum access like
/// any other unrecognized token.
/// </param>
public sealed record LocalhostAuthOptions(
    string SelectorToken,
    string? ExpectedLocalhostToken = null,
    string? DefaultGrantId = null)
{
    /// <summary>
    /// The built-in selector token value used when none is configured. A non-secret, well-known
    /// string - it selects the default binding only when a <see cref="DefaultGrantId"/> is also
    /// configured, so on its own it is inert.
    /// </summary>
    public const string DefaultSelectorToken = "vault-mcp-localhost-selector";

    /// <summary>The permissive default: the built-in selector, no strict token, and no dev
    /// default binding - so every unrecognized token (including the selector) floors to minimum
    /// access. This is the posture a bare <see cref="GrantStore"/> starts in.</summary>
    public static LocalhostAuthOptions Default { get; } = new(DefaultSelectorToken);

    /// <summary>The token that actually selects the default binding: the strict expected token
    /// when set, otherwise the permissive well-known selector.</summary>
    public string EffectiveSelectorToken =>
        string.IsNullOrEmpty(ExpectedLocalhostToken) ? SelectorToken : ExpectedLocalhostToken;
}
