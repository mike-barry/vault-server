using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Coverage for the configurable Kestrel listener bind. Program.cs resolves
/// the bind as <c>Configuration["ListenUrls"] ?? "http://localhost:5111"</c> and passes it to
/// UseUrls; TestServer replaces Kestrel outright, so the actual bind isn't observable through the
/// hosted fixtures. These tests pin the two things that are: the resolution rule (unset falls back
/// to the built-in default, a set value is honored) and that the committed appsettings.json
/// ships that same default, so a deployment that configures nothing binds where the code says.
/// </summary>
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
