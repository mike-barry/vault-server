using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the configurable Kestrel listener bind, which Program.cs resolves from the
/// <c>ListenUrls</c> setting and passes to UseUrls.
/// </summary>
/// <remarks>
/// TestServer replaces Kestrel, so neither the bind nor Program.cs's resolution of it is observable
/// from a hosted fixture. What is observable, and what this covers, is that the committed
/// appsettings.json ships the same default the code falls back to, so a deployment that configures
/// nothing binds to <c>http://localhost:5111</c>.
/// </remarks>
public sealed class ListenUrlsConfigTests
{
    private const string DefaultBind = "http://localhost:5111";

    [Fact]
    public void CommittedAppSettings_ShipsTheLocalhostDefault()
    {
        var appSettingsPath = Path.Combine(TestVaultFixture.RepoRoot, "src", "VaultMcpServer", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath));

        Assert.True(doc.RootElement.TryGetProperty("ListenUrls", out var listenUrls));
        Assert.Equal(DefaultBind, listenUrls.GetString());
    }
}
