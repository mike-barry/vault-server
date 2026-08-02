using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace VaultMcpServer.Tools;

/// <summary>Result of running a script tool: joined success output, joined error-stream text, and whether any error occurred.</summary>
public sealed record ScriptToolExecution(string OutputText, string ErrorText, bool HadErrors);

/// <summary>
/// Runs script tools in-process via a shared <see cref="RunspacePool"/> - no
/// `pwsh.exe` process spawned per call. Each invocation checks out a pooled runspace for
/// its duration, so concurrent MCP tool calls don't share mutable session state.
/// </summary>
public sealed class ScriptToolRunner : IDisposable
{
    private readonly RunspacePool _pool;

    // The authoritative vaults.json path the host resolved at startup (from the VaultsConfigPath
    // appsetting) - re-asserted into VAULT_MCP_VAULTS_JSON ahead of every invocation (see
    // WrapperScript) so a value a prior tool script left in that process-wide env var cannot
    // redirect a later one at an attacker-chosen config. Null only when a caller constructs the
    // runner without a path (unit tests running scripts that don't touch the vault module), in
    // which case the wrapper clears the var so the VaultAccess module falls back to its own default.
    private readonly string? _vaultsJsonPath;

    // The ripgrep executable path the host resolved at startup (from the RipgrepPath appsetting) -
    // re-asserted into VAULT_MCP_RG_PATH ahead of every invocation for the same
    // sequential-reuse reason as the vaults.json path above. Null when the host started without
    // one, in which case the wrapper clears the var and Resolve-VaultRgPath falls back to PATH.
    private readonly string? _ripgrepPath;

    public ScriptToolRunner(string? vaultsJsonPath = null, string? ripgrepPath = null)
    {
        _vaultsJsonPath = vaultsJsonPath;
        _ripgrepPath = ripgrepPath;

        // The (min, max, InitialSessionState, PSHost) overload requires a non-null host, so
        // the initial session state is set via the (InitialSessionState)-only overload
        // instead, with min/max applied after construction. Bypass is required on Windows:
        // the default (Restricted) execution policy blocks running a .ps1 file even from a
        // hosted runspace, not just an interactive console.
        var state = InitialSessionState.CreateDefault();
        state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        _pool = RunspaceFactory.CreateRunspacePool(state);
        _pool.SetMinRunspaces(1);
        _pool.SetMaxRunspaces(Environment.ProcessorCount);
        _pool.ThreadOptions = PSThreadOptions.UseNewThread;
        _pool.Open();
    }

    /// <summary>
    /// Wraps the target script in a single script-block command rather than invoking it
    /// directly, so the host can inject the calling grant's path-scope roots as a
    /// global-scope variable ahead of it - in the same runspace, same pipeline, one
    /// PowerShell "statement" as far as the SDK's Commands collection is concerned (adding a
    /// second top-level command via <c>AddStatement()</c> against a pooled runspace was found
    /// to crash the SDK's background invocation worker with a NullReferenceException; a
    /// single multi-line script block avoids that). The wrapped script's own parameters are
    /// splatted through <c>@VaultMcpParameters</c> rather than passed as literal
    /// <c>AddParameter</c> calls, so the script never sees "VaultMcpScriptPath",
    /// "VaultMcpScopeRoots", "VaultMcpOpClasses", or "VaultMcpParameters" as its own arguments.
    /// </summary>
    private const string WrapperScript = """
        param(
            [string] $VaultMcpScriptPath,
            [hashtable] $VaultMcpScopeRoots,
            [string[]] $VaultMcpOpClasses,
            [hashtable] $VaultMcpParameters,
            [string] $VaultMcpVaultsJsonPath,
            [string] $VaultMcpRipgrepPath
        )
        # Runspaces are pooled and reused, and $env: changes are process-wide, so a value a
        # previous tool script left in VAULT_MCP_VAULTS_JSON (which is how a script locates
        # vaults.json) would otherwise persist into this call and could redirect it at
        # an attacker-chosen config. Re-assert the host's startup value before every invocation:
        # set it back to the intended path, or clear it when the host started without one (the
        # production default, where the module falls back to its own repo-relative vaults.json).
        # Either way a prior tool's mutation cannot survive into this call. (Concurrent tools on
        # the pool still share process env state; this closes the sequential-reuse vector, which
        # is the one that persists across calls. Test-Path-guard the removal so clearing an
        # already-absent var does not write to the error stream and flip the invocation's
        # HadErrors flag.)
        if ($VaultMcpVaultsJsonPath) { $env:VAULT_MCP_VAULTS_JSON = $VaultMcpVaultsJsonPath }
        elseif (Test-Path -LiteralPath Env:\VAULT_MCP_VAULTS_JSON) { Remove-Item -LiteralPath Env:\VAULT_MCP_VAULTS_JSON }
        # Same sequential-reuse re-assertion for the ripgrep path (RipgrepPath appsetting ->
        # VAULT_MCP_RG_PATH, read by Resolve-VaultRgPath): set the host's startup value, or clear
        # it so resolution falls back to PATH, so a prior tool's mutation cannot redirect a later
        # call at an attacker-chosen binary.
        if ($VaultMcpRipgrepPath) { $env:VAULT_MCP_RG_PATH = $VaultMcpRipgrepPath }
        elseif (Test-Path -LiteralPath Env:\VAULT_MCP_RG_PATH) { Remove-Item -LiteralPath Env:\VAULT_MCP_RG_PATH }
        Set-Variable -Name VaultMcpScopeRoots -Value $VaultMcpScopeRoots -Scope Global
        # Mirror of the scope-roots injection above for the calling grant's op-class set: the
        # runspace-global VaultMcpOpClasses is the in-script authorization floor's "key ring"
        # that Assert-VaultOpClass reads to gate an elevated operation (a tool declaring a floor
        # op-class can still require a higher class mid-script). Injected as a global rather than
        # a script parameter for the same invariant as the scope roots - a tool's param() block
        # is its verbatim MCP input schema, so a client-controllable "OpClasses" argument would
        # let a caller forge its own grant's classes. A null/absent global (standalone pwsh -File,
        # no host) makes Assert-VaultOpClass degrade open exactly as unscoped scope roots do.
        Set-Variable -Name VaultMcpOpClasses -Value $VaultMcpOpClasses -Scope Global
        & $VaultMcpScriptPath @VaultMcpParameters
        """;

    /// <param name="scopeRoots">
    /// The calling grant's per-vault folder-prefix path-scope roots (null/empty for a grant
    /// unscoped everywhere; a vault absent from the map is unscoped for that vault - see
    /// <see cref="Auth.Grant.ScopeRoots"/>). Not a script parameter - a tool script's own
    /// <c>param()</c> block is exposed verbatim as its MCP input schema (see
    /// <c>ScriptToolLoader</c>), so a client-controllable "ScopeRoots" argument would let a
    /// caller override its own grant's scope. Instead this is injected as a global-scope
    /// runspace variable (<c>VaultMcpScopeRoots</c>) by <see cref="WrapperScript"/>, invisible
    /// to the script's own parameter surface, as a hashtable keyed by vault name so a script can
    /// pick out the entry for whichever vault its call is targeting. Injecting it out of reach of
    /// the parameter surface is what this side guarantees; enforcing the roots against the paths
    /// a script touches is the script's own.
    /// </param>
    /// <param name="opClasses">
    /// The calling grant's op-class set (see <see cref="Auth.Grant.OpClasses"/>), injected into the
    /// runspace as the <c>VaultMcpOpClasses</c> global (see <see cref="WrapperScript"/>) so a tool
    /// script's <c>Assert-VaultOpClass</c> calls can gate an elevated in-script operation against the
    /// same authority the dispatcher's floor check (<c>grant.Allows(tool.OpClass)</c>) used to admit
    /// the call. Not a script parameter, for the same reason as <paramref name="scopeRoots"/>: a
    /// tool's own <c>param()</c> block is its verbatim MCP input schema, so a client-controllable
    /// argument would let a caller forge its grant's classes. Null (the default) leaves the global
    /// unset - a standalone <c>pwsh -File</c> run has no injected context, so <c>Assert-VaultOpClass</c>
    /// degrades open, mirroring how an unscoped scope-roots map degrades. An empty (non-null) set is
    /// distinct: injected as an empty array, an empty key ring DENIES every asserted class.
    /// </param>
    public async Task<ScriptToolExecution> InvokeAsync(string scriptPath, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken, IReadOnlyDictionary<string, Vault.VaultScope>? scopeRoots = null, IReadOnlySet<string>? opClasses = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;

        var scriptParameters = new Hashtable();
        foreach (var (name, value) in parameters)
            scriptParameters[name] = value;

        // The runspace wire format is the flat root array, where an empty array means unscoped -
        // a script-side convention that predates the two-case model and stays put, since it is an
        // external contract rather than an internal representation. The projection happens here,
        // at the boundary, so the sentinel never travels back inward.
        var scopeRootsTable = new Hashtable(StringComparer.OrdinalIgnoreCase);
        if (scopeRoots is not null)
        {
            foreach (var (vault, scope) in scopeRoots)
                scopeRootsTable[vault] = scope is Vault.VaultScope.RootedScope rooted ? rooted.Roots.ToArray() : [];
        }

        // Preserve the null-vs-empty distinction a script depends on: a null set binds the
        // wrapper's [string[]] param to $null (no injected context at all), while an empty set
        // binds an empty array (an injected but empty key ring that denies every asserted class).
        // ToArray() on an empty set yields an empty array, not null.
        var opClassesArray = opClasses?.ToArray();

        ps.AddScript(WrapperScript)
            .AddParameter("VaultMcpScriptPath", scriptPath)
            .AddParameter("VaultMcpScopeRoots", scopeRootsTable)
            .AddParameter("VaultMcpOpClasses", opClassesArray)
            .AddParameter("VaultMcpParameters", scriptParameters)
            .AddParameter("VaultMcpVaultsJsonPath", _vaultsJsonPath ?? "")
            .AddParameter("VaultMcpRipgrepPath", _ripgrepPath ?? "");

        await using var registration = cancellationToken.Register(() => ps.Stop());

        PSDataCollection<PSObject> output;
        try
        {
            output = await ps.InvokeAsync().ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            return new ScriptToolExecution(OutputText: "", ErrorText: ex.Message, HadErrors: true);
        }

        var outputText = string.Join(Environment.NewLine, output.Select(o => o?.ToString() ?? string.Empty));
        var errorText = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
        return new ScriptToolExecution(outputText, errorText, ps.HadErrors);
    }

    public void Dispose() => _pool.Dispose();
}
