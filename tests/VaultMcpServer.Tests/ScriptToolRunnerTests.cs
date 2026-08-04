using VaultMcpServer.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers <see cref="ScriptToolRunner"/>: what it returns for a script's pipeline output, how it
/// captures errors instead of throwing, and that concurrent invocations stay independent.
/// </summary>
public sealed class ScriptToolRunnerTests : IDisposable, IClassFixture<ScriptToolRunnerTests.RunnerFixture>
{
    /// <summary>Holds one runner shared by every test in the class, and disposes it at the end.</summary>
    public sealed class RunnerFixture : IDisposable
    {
        public ScriptToolRunner Runner { get; } = new();
        public void Dispose() => Runner.Dispose();
    }

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-runner-tests-").FullName;
    private readonly ScriptToolRunner _runner;

    public ScriptToolRunnerTests(RunnerFixture fixture) => _runner = fixture.Runner;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Writes a script into this test's temp directory.</summary>
    /// <param name="content">The script's text.</param>
    /// <returns>The full path written.</returns>
    private string WriteScript(string content)
    {
        var path = Path.Combine(_tempDir, "script.ps1");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task InvokeAsync_ReturnsPipelineOutputJoined()
    {
        var path = WriteScript("param([string]$Name) Write-Output \"hello $Name\"");
        var result = await _runner.InvokeAsync(path, new Dictionary<string, object?> { ["Name"] = "world" }, CancellationToken.None);

        Assert.False(result.HadErrors);
        Assert.Equal("hello world", result.OutputText);
    }

    [Fact]
    public async Task InvokeAsync_MultipleOutputLines_AreNewlineJoined()
    {
        var path = WriteScript("Write-Output 'a'; Write-Output 'b'");
        var result = await _runner.InvokeAsync(path, new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal($"a{Environment.NewLine}b", result.OutputText);
    }

    [Fact]
    public async Task InvokeAsync_NonTerminatingError_IsCapturedWithoutThrowing()
    {
        var path = WriteScript("Write-Error 'boom'; Write-Output 'still ran'");
        var result = await _runner.InvokeAsync(path, new Dictionary<string, object?>(), CancellationToken.None);

        Assert.True(result.HadErrors);
        Assert.Contains("boom", result.ErrorText);
        Assert.Equal("still ran", result.OutputText);
    }

    [Fact]
    public async Task InvokeAsync_TerminatingError_IsCapturedAsError()
    {
        var path = WriteScript("throw 'kaboom'");
        var result = await _runner.InvokeAsync(path, new Dictionary<string, object?>(), CancellationToken.None);

        Assert.True(result.HadErrors);
        Assert.Contains("kaboom", result.ErrorText);
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentCalls_DoNotInterfere()
    {
        var path = WriteScript("param([string]$Id) Write-Output $Id");
        var tasks = Enumerable.Range(0, 8)
            .Select(i => _runner.InvokeAsync(path, new Dictionary<string, object?> { ["Id"] = i.ToString() }, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
            Assert.Equal(i.ToString(), results[i].OutputText);
    }
}
