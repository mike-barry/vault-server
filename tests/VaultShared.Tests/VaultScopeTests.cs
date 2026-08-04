using VaultShared.Auth;
using VaultShared.Scope;

namespace VaultShared.Tests;

public sealed class VaultScopeTests
{
    [Theory]
    [MemberData(nameof(ScopeCases))]
    public void Admits_ContainmentCases(string name, string vault, string path, IReadOnlyDictionary<string, VaultScope> scopeRoots, Outcome expected)
    {
        var resolved = GrantOver(scopeRoots).ScopeIn(vault);

        switch (expected)
        {
            case Outcome.InScope:
                Assert.True(resolved.Admits(path), $"{name}: expected in scope");
                break;
            case Outcome.OutOfScope:
                Assert.False(resolved.Admits(path), $"{name}: expected out of scope");
                break;
            case Outcome.Invalid:
                Assert.Throws<ArgumentException>(() => resolved.Admits(path));
                break;
        }
    }

    /// <summary>
    /// Specifies what a scope case expects. An enum rather than a string, so a case cannot name an
    /// outcome the theory does not handle.
    /// </summary>
    public enum Outcome
    {
        /// <summary>The scope admits the path.</summary>
        InScope,

        /// <summary>The scope does not admit the path.</summary>
        OutOfScope,

        /// <summary>The check throws, because a configured root is not a contained relative path.</summary>
        Invalid,
    }

    [Fact]
    public void VaultScope_EmptyRootSetIsNotConstructible()
    {
        // Whole-vault authority has exactly one spelling, VaultScope.Unscoped, and an empty root
        // set is not it. A caller that filters a root list down to nothing gets an error rather
        // than the widest possible grant.
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

    public static TheoryData<string, string, string, IReadOnlyDictionary<string, VaultScope>, Outcome> ScopeCases => new()
    {
        { "unscoped grant allows anything", "test", "Inbox/Note-Loose-Thought.md", Scopes(("test", VaultScope.Unscoped)), Outcome.InScope },
        { "path directly under a single-segment root", "test", "Projects/Widget/Widget.md", Scopes(("test", Rooted("Projects"))), Outcome.InScope },
        { "path directly under a multi-segment root", "test", "Projects/Widget/Inbox/Plan-Widget-Assembly.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.InScope },
        { "path equal to the root plus one segment", "test", "Projects/Widget/Tasks.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.InScope },
        { "path under a different top-level folder is out of scope", "test", "Inbox/Note-Loose-Thought.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.OutOfScope },
        { "sibling folder sharing a string prefix is out of scope", "test", "Projects/Widget-Retired/Notes.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.OutOfScope },
        { "note whose name string-prefixes the root but sits outside it is out of scope", "test", "Projects/Widget.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.OutOfScope },
        { "path matches when any of several roots contains it", "test", "Resources/Vault/Guides/Vault-Guide.md", Scopes(("test", Rooted("Projects/Widget", "Resources/Vault/Guides"))), Outcome.InScope },
        { "path matches none of several roots", "test", "Inbox/Note-Loose-Thought.md", Scopes(("test", Rooted("Projects/Widget", "Resources/Vault/Guides"))), Outcome.OutOfScope },
        { "case-insensitive segment match", "test", "projects/WIDGET/widget.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.InScope },
        { "backslash-separated path normalizes the same as forward-slash", "test", @"Projects\Widget\Widget.md", Scopes(("test", Rooted("Projects/Widget"))), Outcome.InScope },
        { "root with a trailing dot-dot that stays inside the vault normalizes cleanly", "test", "Projects/Widget/Widget.md", Scopes(("test", Rooted("Projects/Elsewhere/../Widget"))), Outcome.InScope },
        { "root that escapes the vault is unscopeable", "test", "Projects/Widget/Widget.md", Scopes(("test", Rooted("../outside"))), Outcome.Invalid },
        { "rooted root path is unscopeable", "test", "Projects/Widget/Widget.md", Scopes(("test", Rooted("C:/Windows"))), Outcome.Invalid },
        { "empty-string root is unscopeable", "test", "Projects/Widget/Widget.md", Scopes(("test", Rooted(""))), Outcome.Invalid },
        { "one malformed root among otherwise-matching roots still errors", "test", "Projects/Widget/Widget.md", Scopes(("test", Rooted("Projects/Widget", "../outside"))), Outcome.Invalid },
        { "an explicit whole-vault entry for a vault means fully unscoped, even though another vault has a scope root configured", "scratch", "Inbox/Note-Loose-Thought.md", Scopes(("test", Rooted("Projects")), ("scratch", VaultScope.Unscoped)), Outcome.InScope },
        { "a scope root configured for one vault still restricts that same vault directly", "test", "Inbox/Note-Loose-Thought.md", Scopes(("test", Rooted("Projects"))), Outcome.OutOfScope },
        { "a scope root configured for one vault does not leak into a second vault's own (different) scope root", "scratch", "Projects/Widget/Widget.md", Scopes(("test", Rooted("Projects/Widget")), ("scratch", Rooted("Resources"))), Outcome.OutOfScope },
    };

    private static VaultScope Rooted(params string[] roots) => VaultScope.Of(roots.ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a grant reaching exactly the vaults <paramref name="scopeRoots"/> covers, which is
    /// the relationship every real grant holds between its vaults and its scopes.
    /// </summary>
    /// <param name="scopeRoots">The grant's scope in each vault, keyed by vault name.</param>
    /// <returns>The grant.</returns>
    private static Grant GrantOver(IReadOnlyDictionary<string, VaultScope> scopeRoots) =>
        new("test-grant", new HashSet<string>(), scopeRoots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), null, scopeRoots);

    private static IReadOnlyDictionary<string, VaultScope> Scopes(params (string Vault, VaultScope Scope)[] entries) =>
        entries.ToDictionary(e => e.Vault, e => e.Scope, StringComparer.OrdinalIgnoreCase);
}
