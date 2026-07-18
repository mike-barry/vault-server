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

    public async Task<ScriptToolExecution> InvokeAsync(string scriptPath, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _pool;
        ps.AddCommand(scriptPath);
        foreach (var (name, value) in parameters)
            ps.AddParameter(name, value);

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
