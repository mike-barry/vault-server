using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class VaultAccessTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    [Theory]
    [MemberData(nameof(PathCases))]
    public void ResolvePath_ContainmentCases(string name, string path, bool valid)
    {
        if (valid)
        {
            var full = fixture.Vault.ResolvePath(null, path);
            Assert.True(Path.IsPathRooted(full), $"{name}: expected an absolute path, got {full}");
        }
        else
        {
            var ex = Record.Exception(() => fixture.Vault.ResolvePath(null, path));
            Assert.True(ex is ArgumentException, $"{name}: expected ArgumentException, got {ex?.GetType().Name ?? "no exception"}");
        }
    }

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public void IsPathInScope_ContainmentCases(string name, string vault, string path, IReadOnlyDictionary<string, VaultScope> scopeRoots, Outcome expected)
    {
        var resolved = VaultAccess.ResolveVaultScopeRoots(vault, scopeRoots);

        switch (expected)
        {
            case Outcome.InScope:
                Assert.True(VaultAccess.IsPathInScope(path, resolved), $"{name}: expected in scope");
                break;
            case Outcome.OutOfScope:
                Assert.False(VaultAccess.IsPathInScope(path, resolved), $"{name}: expected out of scope");
                break;
            case Outcome.Invalid:
                Assert.Throws<ArgumentException>(() => VaultAccess.IsPathInScope(path, resolved));
                break;
        }
    }

    /// <summary>What a scope case asserts. An enum rather than a string, so a case cannot name an
    /// outcome the theory does not handle.</summary>
    public enum Outcome { InScope, OutOfScope, Invalid }

    [Fact]
    public void VaultScope_EmptyRootSetIsNotConstructible()
    {
        // The point of the two-case shape: whole-vault authority has exactly one spelling, and
        // it is not "a root set that happens to be empty". A caller that filters a root list down
        // to nothing gets an error, not the widest possible grant.
        Assert.Throws<ArgumentException>(() => VaultScope.Of([]));
        Assert.Throws<ArgumentException>(() => VaultScope.Of(new HashSet<string>()));
    }

    [Fact]
    public void VaultScope_RootedScopesCompareByTheirRoots()
    {
        // Guards the record's compiler-generated equality, which would compare the underlying
        // set by reference and report two identical scopes as different.
        Assert.Equal(VaultScope.Of(["Projects", "Areas"]), VaultScope.Of(["Areas", "Projects"]));
        Assert.NotEqual(VaultScope.Of(["Projects"]), VaultScope.Of(["Areas"]));
        Assert.NotEqual(VaultScope.Unscoped, VaultScope.Of(["Projects"]));
    }

    [Fact]
    public void ResolveVaultScopeRoots_NonNullMapMissingVaultKeyThrows()
    {
        // Every real Grant.ScopeRoots is guaranteed to have an entry for every vault it can
        // reach (enforced by GrantStore.Load and GrantStore.Mint), so a non-null map missing
        // the queried vault's key means that invariant was violated upstream - it must fail
        // loudly rather than silently falling back to full access.
        Assert.Throws<InvalidOperationException>(
            () => VaultAccess.ResolveVaultScopeRoots("test", Scopes(("primary", Rooted("Projects")))));
    }

    public static TheoryData<string, string, bool> PathCases => new()
    {
        { "simple relative path", "Inbox/Note-Loose-Thought.md", true },
        { "backslash separators", @"Inbox\Note-Loose-Thought.md", true },
        { "dot-dot that stays inside the vault", "Projects/../Test-Vault.md", true },
        { "nonexistent relative path still resolves", "Not/Yet/Written.md", true },
        { "rooted path rejected", "C:/Windows/win.ini", false },
        { "single dot-dot escape rejected", "../outside.md", false },
        { "nested dot-dot escape rejected", "Projects/../../outside.md", false },
        { "whitespace-only path rejected", "   ", false },
    };

    public static TheoryData<string, string, string, IReadOnlyDictionary<string, VaultScope>, Outcome> ScopeCases => new()
    {
        { "unscoped grant allows anything", "primary", "Areas/Personal/Health.md", Scopes(("primary", VaultScope.Unscoped)), Outcome.InScope },
        { "path directly under a single-segment root", "primary", "Projects/Widget/Widget.md", Scopes(("primary", Rooted("Projects"))), Outcome.InScope },
        { "path directly under a multi-segment root", "primary", "Projects/Vault-MCP-Server/Corpus/Server-Structure.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server"))), Outcome.InScope },
        { "path equal to the root plus one segment", "primary", "Projects/Vault-MCP-Server/Tasks.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server"))), Outcome.InScope },
        { "path under a different top-level folder is out of scope", "primary", "Areas/Personal/Health.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server"))), Outcome.OutOfScope },
        { "sibling folder sharing a string prefix is out of scope", "primary", "Projects/Vault-MCP-Server-Old/Notes.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server"))), Outcome.OutOfScope },
        { "note whose name string-prefixes the root but sits outside it is out of scope", "primary", "Projects/Vault-MCP-Server.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server"))), Outcome.OutOfScope },
        { "path matches when any of several roots contains it", "primary", "Areas/Homelab/Servers.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server", "Areas/Homelab"))), Outcome.InScope },
        { "path matches none of several roots", "primary", "Areas/Personal/Health.md", Scopes(("primary", Rooted("Projects/Vault-MCP-Server", "Areas/Homelab"))), Outcome.OutOfScope },
        { "case-insensitive segment match", "primary", "projects/WIDGET/widget.md", Scopes(("primary", Rooted("Projects/Widget"))), Outcome.InScope },
        { "backslash-separated path normalizes the same as forward-slash", "primary", @"Projects\Widget\Widget.md", Scopes(("primary", Rooted("Projects/Widget"))), Outcome.InScope },
        { "root with a trailing dot-dot that stays inside the vault normalizes cleanly", "primary", "Projects/Widget/Widget.md", Scopes(("primary", Rooted("Projects/Foo/../Widget"))), Outcome.InScope },
        { "root that escapes the vault is unscopeable", "primary", "Projects/Widget/Widget.md", Scopes(("primary", Rooted("../outside"))), Outcome.Invalid },
        { "rooted root path is unscopeable", "primary", "Projects/Widget/Widget.md", Scopes(("primary", Rooted("C:/Windows"))), Outcome.Invalid },
        { "empty-string root is unscopeable", "primary", "Projects/Widget/Widget.md", Scopes(("primary", Rooted(""))), Outcome.Invalid },
        { "one malformed root among otherwise-matching roots still errors", "primary", "Projects/Widget/Widget.md", Scopes(("primary", Rooted("Projects/Widget", "../outside"))), Outcome.Invalid },
        { "an explicit whole-vault entry for a vault means fully unscoped, even though another vault has a scope root configured", "test", "Areas/Personal/Health.md", Scopes(("primary", Rooted("Projects")), ("test", VaultScope.Unscoped)), Outcome.InScope },
        { "a scope root configured for one vault still restricts that same vault directly", "primary", "Areas/Personal/Health.md", Scopes(("primary", Rooted("Projects"))), Outcome.OutOfScope },
        { "a scope root configured for one vault does not leak into a second vault's own (different) scope root", "test", "Projects/Widget/Widget.md", Scopes(("primary", Rooted("Projects/Widget")), ("test", Rooted("Areas/Homelab"))), Outcome.OutOfScope },
    };

    private static VaultScope Rooted(params string[] roots) => VaultScope.Of(roots.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, VaultScope> Scopes(params (string Vault, VaultScope Scope)[] entries) =>
        entries.ToDictionary(e => e.Vault, e => e.Scope, StringComparer.OrdinalIgnoreCase);
}
