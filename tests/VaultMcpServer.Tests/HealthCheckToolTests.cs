using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Unit-level coverage for <see cref="HealthCheckTool"/>'s own output shape - as distinct
/// from <c>HealthCheckDispatchTests</c>' HTTP-level, end-to-end coverage (op-class bypass,
/// BROKEN listing, initialize instructions) of the same feature set.
/// </summary>
public sealed class HealthCheckToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    private readonly VaultConfig _vaultConfig = fixture.Config;

    private static Grant MakeGrant(string id = "caller") =>
        new(id, new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, null,
            new Dictionary<string, VaultScope> { ["test"] = VaultScope.Unscoped });

    [Fact]
    public async Task InvokeAsync_HealthyState_ReportsNotFatallyDegradedAndNoProblems()
    {
        var healthState = new ServerHealthState();
        var tool = new HealthCheckTool(healthState, _vaultConfig);

        var result = await tool.InvokeAsync(new Dictionary<string, JsonElement>(), MakeGrant(), CancellationToken.None);

        Assert.False(result.IsError == true);
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(text!);
        Assert.False(doc.RootElement.GetProperty("isFatallyDegraded").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task InvokeAsync_WithRecordedProblems_ReportsThemInDetailIncludingPathsVerbatim()
    {
        var healthState = new ServerHealthState();
        healthState.RecordFatal("GrantStore", @"grants.json not found: C:\some\absolute\path\grants.json");
        healthState.RecordDegraded("VaultConfig", @"vault ""broken"" skipped - root directory does not exist: C:\vaults\broken");
        var tool = new HealthCheckTool(healthState, _vaultConfig);

        var result = await tool.InvokeAsync(new Dictionary<string, JsonElement>(), MakeGrant(), CancellationToken.None);

        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text!;
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("isFatallyDegraded").GetBoolean());
        var problems = doc.RootElement.GetProperty("problems").EnumerateArray().ToList();
        Assert.Equal(2, problems.Count);

        // Settled decision: absolute filesystem paths in problem Detail strings flow
        // verbatim into this tool's output - no sanitized second rendering path.
        Assert.Contains(problems, p => p.GetProperty("detail").GetString()!.Contains(@"C:\some\absolute\path\grants.json"));
        Assert.Contains(problems, p => p.GetProperty("detail").GetString()!.Contains(@"C:\vaults\broken"));
        Assert.Contains(problems, p => p.GetProperty("severity").GetString() == "Fatal");
        Assert.Contains(problems, p => p.GetProperty("severity").GetString() == "Degraded");
    }

    [Fact]
    public async Task InvokeAsync_ReportsConfiguredVaults()
    {
        var healthState = new ServerHealthState();
        var tool = new HealthCheckTool(healthState, _vaultConfig);

        var result = await tool.InvokeAsync(new Dictionary<string, JsonElement>(), MakeGrant(), CancellationToken.None);

        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text!;
        using var doc = JsonDocument.Parse(text);
        var configured = doc.RootElement.GetProperty("vaults").GetProperty("configured").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("test", configured);
        Assert.Contains("scratch", configured);
        Assert.Equal(_vaultConfig.DefaultVault, doc.RootElement.GetProperty("vaults").GetProperty("defaultVault").GetString());
    }
}
