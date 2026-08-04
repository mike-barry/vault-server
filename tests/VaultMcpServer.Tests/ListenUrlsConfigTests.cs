using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the configurable Kestrel listener bind. Program.cs resolves it as
/// <c>Configuration["ListenUrls"] ?? "http://localhost:5111"</c> and passes the result to UseUrls.
/// </summary>
/// <remarks>
/// TestServer replaces Kestrel, so the actual bind is not observable through the hosted fixtures.
/// These tests cover what is: the resolution rule, and that the committed appsettings.json ships
/// the same default, so a deployment that configures nothing binds where the code says.
/// </remarks>
public sealed class ListenUrlsConfigTests
{
    private const string DefaultBind = "http://localhost:5111";

    private static string ResolveBind(IConfiguration configuration) =>
        configuration["ListenUrls"] ?? DefaultBind;

    [Fact]
    public void UnsetListenUrls_FallsBackToLocalhost5111()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(DefaultBind, ResolveBind(configuration));
    }

    [Fact]
    public void SetListenUrls_IsHonored()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ListenUrls"] = "http://0.0.0.0:8080" })
            .Build();

        Assert.Equal("http://0.0.0.0:8080", ResolveBind(configuration));
    }

    [Fact]
    public void CommittedAppSettings_ShipsTheLocalhostDefault()
    {
        var appSettingsPath = Path.Combine(TestVaultFixture.RepoRoot, "src", "VaultMcpServer", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath));

        Assert.True(doc.RootElement.TryGetProperty("ListenUrls", out var listenUrls));
        Assert.Equal(DefaultBind, listenUrls.GetString());
    }
}
