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

    public ScriptToolRunner()
    {
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
    /// "VaultMcpScopeRoots", or "VaultMcpParameters" as its own arguments.
    /// </summary>
    private const string WrapperScript = """
        param(
            [string] $VaultMcpScriptPath,
            [string[]] $VaultMcpScopeRoots,
            [hashtable] $VaultMcpParameters
        )
        Set-Variable -Name VaultMcpScopeRoots -Value $VaultMcpScopeRoots -Scope Global
        & $VaultMcpScriptPath @VaultMcpParameters
        """;

    /// <param name="scopeRoots">
    /// The calling grant's folder-prefix path-scope roots (null/empty for an unscoped grant).
    /// Not a script parameter - a tool script's own <c>param()</c> block is exposed verbatim
    /// as its MCP input schema (see <c>ScriptToolLoader</c>), so a client-controllable
    /// "ScopeRoots" argument would let a caller override its own grant's scope. Instead this
    /// is injected as a global-scope runspace variable (<c>VaultMcpScopeRoots</c>) by
    /// <see cref="WrapperScript"/>, invisible to the script's own parameter surface. The
    /// VaultAccess module's chokepoint functions default their own <c>-ScopeRoots</c>
    /// parameter to this variable's value when the caller (a tool script) doesn't pass one
    /// explicitly, so path-scope enforcement applies without every tool script needing to
    /// plumb it through by hand.
    /// </param>
    public async Task<ScriptToolExecution> InvokeAsync(string scriptPath, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken, IReadOnlyCollection<string>? scopeRoots = null)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;

        var scriptParameters = new Hashtable();
        foreach (var (name, value) in parameters)
            scriptParameters[name] = value;

        ps.AddScript(WrapperScript)
            .AddParameter("VaultMcpScriptPath", scriptPath)
            .AddParameter("VaultMcpScopeRoots", scopeRoots?.ToArray() ?? [])
            .AddParameter("VaultMcpParameters", scriptParameters);

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
