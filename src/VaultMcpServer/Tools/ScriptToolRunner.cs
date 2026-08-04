using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using VaultShared.Scope;

namespace VaultMcpServer.Tools;

/// <summary>Represents the outcome of running a script tool.</summary>
/// <param name="OutputText">The success-stream output, joined with newlines.</param>
/// <param name="ErrorText">The error-stream text, joined with newlines.</param>
/// <param name="HadErrors">Whether the invocation reported any error.</param>
public sealed record ScriptToolExecution(string OutputText, string ErrorText, bool HadErrors);

/// <summary>
/// Runs script tools in-process through a shared <see cref="RunspacePool"/>, without spawning a
/// <c>pwsh.exe</c> process per call. Each invocation checks out a pooled runspace for its
/// duration, so concurrent MCP tool calls do not share mutable session state.
/// </summary>
public sealed class ScriptToolRunner : IDisposable
{
    private readonly RunspacePool _pool;

    // The vaults.json path the host resolved at startup. The wrapper script re-asserts it into
    // VAULT_MCP_VAULTS_JSON before every invocation, so a value a prior tool script left in that
    // process-wide variable cannot redirect a later call at another config file. Null only when
    // a caller constructs the runner without a path, such as a unit test running a script that
    // needs no vault, in which case the wrapper clears the variable.
    private readonly string? _vaultsJsonPath;

    // The ripgrep executable path the host resolved at startup, re-asserted into
    // VAULT_MCP_RG_PATH before every invocation for the same reason as the vaults.json path
    // above. Null when the host started without one, in which case the wrapper clears the
    // variable and a script falls back to rg on PATH.
    private readonly string? _ripgrepPath;

    /// <summary>Opens the runspace pool the runner invokes scripts through.</summary>
    /// <param name="vaultsJsonPath">The vaults.json path to pin for every invocation, or null to
    /// clear the variable and let a script use its own default.</param>
    /// <param name="ripgrepPath">The ripgrep executable path to pin for every invocation, or
    /// null to clear the variable and let a script fall back to rg on PATH.</param>
    public ScriptToolRunner(string? vaultsJsonPath = null, string? ripgrepPath = null)
    {
        _vaultsJsonPath = vaultsJsonPath;
        _ripgrepPath = ripgrepPath;

        // The (min, max, InitialSessionState, PSHost) overload requires a non-null host, so the
        // initial session state is set through the (InitialSessionState)-only overload and the
        // min and max are applied after construction. Bypass is required on Windows: the default
        // Restricted execution policy blocks running a .ps1 file from a hosted runspace, not
        // just from an interactive console.
        var state = InitialSessionState.CreateDefault();
        state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        _pool = RunspaceFactory.CreateRunspacePool(state);
        _pool.SetMinRunspaces(1);
        _pool.SetMaxRunspaces(Environment.ProcessorCount);
        _pool.ThreadOptions = PSThreadOptions.UseNewThread;
        _pool.Open();
    }

    /// <summary>
    /// The script block that sets up the runspace and then invokes the target script.
    /// </summary>
    /// <remarks>
    /// The target script is wrapped rather than invoked directly so the host can inject the
    /// calling grant's scope roots and op classes as global variables first, in the same
    /// runspace and pipeline. Keep this as one script block: adding a second top-level command
    /// through <c>AddStatement()</c> against a pooled runspace crashes the SDK's background
    /// invocation worker with a NullReferenceException.
    ///
    /// The wrapped script's own parameters are splatted through <c>@VaultMcpParameters</c>
    /// rather than passed as individual <c>AddParameter</c> calls, so the script never sees the
    /// wrapper's own parameters as its arguments.
    /// </remarks>
    private const string WrapperScript = """
        param(
            [string] $VaultMcpScriptPath,
            [hashtable] $VaultMcpScopeRoots,
            [string[]] $VaultMcpOpClasses,
            [hashtable] $VaultMcpParameters,
            [string] $VaultMcpVaultsJsonPath,
            [string] $VaultMcpRipgrepPath
        )
        # Runspaces are pooled and reused and $env: changes are process-wide, so a value a
        # previous tool script left in VAULT_MCP_VAULTS_JSON, which is how a script locates
        # vaults.json, would otherwise persist into this call and could point it at another
        # config file. Re-assert the host's startup value before every invocation: set the
        # intended path, or clear the variable when the host started without one, which is the
        # production default and leaves a script to its own repo-relative vaults.json.
        # Concurrent tools on the pool still share process environment state; this closes the
        # sequential-reuse case, which is the one that persists across calls. The Test-Path guard
        # keeps a removal of an already-absent variable from writing to the error stream and
        # flipping the invocation's HadErrors flag.
        if ($VaultMcpVaultsJsonPath) { $env:VAULT_MCP_VAULTS_JSON = $VaultMcpVaultsJsonPath }
        elseif (Test-Path -LiteralPath Env:\VAULT_MCP_VAULTS_JSON) { Remove-Item -LiteralPath Env:\VAULT_MCP_VAULTS_JSON }
        # The same re-assertion for the ripgrep path in VAULT_MCP_RG_PATH, which a script reads
        # to locate ripgrep: set the host's startup value, or clear it so resolution falls back
        # to PATH, so a prior tool cannot point a later call at another binary.
        if ($VaultMcpRipgrepPath) { $env:VAULT_MCP_RG_PATH = $VaultMcpRipgrepPath }
        elseif (Test-Path -LiteralPath Env:\VAULT_MCP_RG_PATH) { Remove-Item -LiteralPath Env:\VAULT_MCP_RG_PATH }
        Set-Variable -Name VaultMcpScopeRoots -Value $VaultMcpScopeRoots -Scope Global
        # The calling grant's op classes, injected the same way as the scope roots above. A
        # script reads VaultMcpOpClasses to gate an operation that needs more authority than the
        # class the tool itself declares. It is a global rather than a script parameter for the
        # same reason as the scope roots: a tool's param() block is its MCP input schema
        # verbatim, so a client-controllable "OpClasses" argument would let a caller claim
        # classes its grant does not hold. When the global is absent, as under a standalone
        # pwsh -File run with no host, an in-script assertion degrades open, the same way
        # unscoped scope roots do.
        Set-Variable -Name VaultMcpOpClasses -Value $VaultMcpOpClasses -Scope Global
        & $VaultMcpScriptPath @VaultMcpParameters
        """;

    /// <summary>Runs a script tool in a pooled runspace and collects its output.</summary>
    /// <param name="scriptPath">The full path of the script to run.</param>
    /// <param name="parameters">The arguments to splat onto the script.</param>
    /// <param name="cancellationToken">Stops the running pipeline when cancelled.</param>
    /// <param name="scopeRoots">
    /// The calling grant's per-vault folder-prefix scope roots. Null or empty means the grant is
    /// unscoped everywhere, and a vault absent from the map is unscoped for that vault (see
    /// <see cref="Auth.Grant.ScopeRoots"/>). This is injected as the global runspace variable
    /// <c>VaultMcpScopeRoots</c>, a hashtable keyed by vault name, rather than passed as a script
    /// parameter: a tool script's <c>param()</c> block is its MCP input schema verbatim, so a
    /// client-controllable "ScopeRoots" argument would let a caller override its own grant's
    /// scope. This side only guarantees that the value is out of reach of the parameter surface;
    /// enforcing the roots against the paths it touches is the script's own job.
    /// </param>
    /// <param name="opClasses">
    /// The calling grant's op classes (see <see cref="Auth.Grant.OpClasses"/>), injected as the
    /// global runspace variable <c>VaultMcpOpClasses</c> so a script can gate an operation
    /// needing more authority than the class it declares, which the dispatcher's floor check
    /// already admitted the call by. It is not a script parameter for the same reason as
    /// <paramref name="scopeRoots"/>. Null, the default, leaves the global unset, so an
    /// in-script assertion degrades open as it does under a standalone <c>pwsh -File</c> run. An
    /// empty non-null set is different: it is injected as an empty array, which denies every
    /// asserted class.
    /// </param>
    /// <returns>The script's output, error text, and error flag.</returns>
    public async Task<ScriptToolExecution> InvokeAsync(string scriptPath, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken, IReadOnlyDictionary<string, VaultScope>? scopeRoots = null, IReadOnlySet<string>? opClasses = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;

        var scriptParameters = new Hashtable();
        foreach (var (name, value) in parameters)
            scriptParameters[name] = value;

        // The runspace wire format is a flat array of roots per vault, where an empty array
        // means unscoped. That is a script-side contract rather than an internal representation,
        // so the projection happens here at the boundary and the empty-array sentinel never
        // travels back inward.
        var scopeRootsTable = new Hashtable(StringComparer.OrdinalIgnoreCase);
        if (scopeRoots is not null)
        {
            foreach (var (vault, scope) in scopeRoots)
                scopeRootsTable[vault] = scope is VaultScope.RootedScope rooted ? rooted.Roots.ToArray() : [];
        }

        // A script depends on the difference between null and empty: a null set binds the
        // wrapper's [string[]] parameter to $null, meaning no injected context at all, while an
        // empty set binds an empty array, which denies every asserted class. ToArray() on an
        // empty set yields an empty array, not null.
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

    /// <summary>Closes the runspace pool.</summary>
    public void Dispose() => _pool.Dispose();
}
