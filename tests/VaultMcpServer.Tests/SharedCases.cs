using System.Text.Json;

namespace VaultMcpServer.Tests;

/// <summary>
/// Loads the shared test-case files from tests/cases/. The Pester suite
/// (powershell/VaultAccess/Tests/SharedCases.Tests.ps1) consumes the same files, so the
/// mirrored chokepoint semantics are exercised identically on both sides - add a case
/// there once and both implementations must pass it.
/// </summary>
public static class SharedCases
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed record PathCase(string Name, string Path, bool Valid);
    private sealed record ScopeCase(string Name, string Vault, string Path, Dictionary<string, List<string>> ScopeRoots, string Outcome);

    public static TheoryData<string, string, bool> Paths()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var c in Load<PathCase>("path-cases.json"))
            data.Add(c.Name, c.Path, c.Valid);
        return data;
    }

    public static TheoryData<string, string, string, Dictionary<string, List<string>>, string> Scope()
    {
        var data = new TheoryData<string, string, string, Dictionary<string, List<string>>, string>();
        foreach (var c in Load<ScopeCase>("scope-cases.json"))
            data.Add(c.Name, c.Vault, c.Path, c.ScopeRoots, c.Outcome);
        return data;
    }

    private static List<T> Load<T>(string file)
    {
        var json = File.ReadAllText(Path.Combine(TestVaultFixture.CasesDir, file));
        return JsonSerializer.Deserialize<List<T>>(json, Options)!;
    }
}
