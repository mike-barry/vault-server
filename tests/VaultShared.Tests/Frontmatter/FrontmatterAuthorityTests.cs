using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Frontmatter;
using VaultShared.Scope;

// VaultShared.Access and VaultShared.Frontmatter both declare a WriteOutcome, and this file needs
// the access namespace for the IKeyAuthority tests.
using WriteOutcome = VaultShared.Frontmatter.WriteOutcome;

namespace VaultShared.Tests;

/// <summary>
/// Verifies how a frontmatter write decides which operation classes it requires. The required set
/// is the union of three contributions - the registry's declared class for a changed key, the
/// class a namespace prefix supplies, and the path policy's mutation requirement - and it is
/// computed from the keys the write actually changed, not from the keys the operation named.
/// </summary>
public sealed class FrontmatterAuthorityTests
{
    private const string Body = "Body text.\n";

    private static string Note(string block) => $"---\n{block}\n---\n{Body}";

    private static WriteOutcome Apply(
        StubAuthority authority, string content, IReadOnlyList<FrontmatterOperation>? operations = null,
        string? suppliedBlock = null, string? newBody = null, PathPolicy? policy = null, string path = "Notes/Note.md") =>
        FrontmatterWrite.Apply(Synthetic.Write(content, authority, operations, suppliedBlock, newBody, path, policy));

    private static FrontmatterOperation Set(string key, string value) => new FrontmatterOperation.SetScalar(key, value);

    // ---- Contributions to the required set ----

    [Fact]
    public void TheRegistrysDeclaredClassIsRequiredForAChangedKey()
    {
        var authority = new StubAuthority(Synthetic.OpenClass);

        var outcome = Apply(authority, Body, [Set("doc.owner", "someone")]);

        Assert.Equal([Synthetic.OpenClass], outcome.AssertedAuthority);
        Assert.Equal(["doc.owner"], Assert.Single(authority.Calls).ChangedKeys);
    }

    [Fact]
    public void ANamespacePrefixAddsToTheRequirementForAKeyDeclaringNone()
    {
        // agent.mode declares no authority of its own; its namespace supplies it.
        var outcome = Apply(new StubAuthority(Synthetic.ElevatedClass), Body, [Set("agent.mode", "strict")]);

        Assert.Equal([Synthetic.ElevatedClass], outcome.AssertedAuthority);
    }

    [Fact]
    public void ContributionsUnionAcrossEveryChangedKey()
    {
        var outcome = Apply(
            new StubAuthority(Synthetic.OpenClass, Synthetic.ElevatedClass), Body,
            [Set("doc.owner", "someone"), Set("agent.mode", "strict"), Set("doc.title", "Widget")]);

        Assert.Equal(
            [Synthetic.ElevatedClass, Synthetic.OpenClass],
            outcome.AssertedAuthority.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void PathPolicysMutationRequiresContributesForTheNoteItself()
    {
        var policy = Synthetic.Policy($$"""[ { "path": "Areas", "mutation_requires": "{{Synthetic.ElevatedClass}}" } ]""");

        var outcome = Apply(
            new StubAuthority(Synthetic.ElevatedClass), Body, [Set("doc.title", "Widget")],
            policy: policy, path: "Areas/Agentic/Note.md");

        Assert.Equal([Synthetic.ElevatedClass], outcome.AssertedAuthority);
    }

    [Fact]
    public void PathPolicysMutationRequiresContributesOnABodyOnlyWriteToo()
    {
        // A body-only write changes no keys, so it contributes no key-level authority, but the
        // path policy still applies to it.
        var policy = Synthetic.Policy($$"""[ { "path": "Areas", "mutation_requires": "{{Synthetic.ElevatedClass}}" } ]""");
        var authority = new StubAuthority(Synthetic.ElevatedClass);

        var outcome = Apply(authority, Note("doc.title: Widget"), newBody: "Rewritten.\n",
            policy: policy, path: "Areas/Agentic/Note.md");

        Assert.Equal([Synthetic.ElevatedClass], outcome.AssertedAuthority);
        Assert.Empty(Assert.Single(authority.Calls).ChangedKeys);
    }

    [Fact]
    public void PathPolicyContributesNothingToACallThatMutatesNothing()
    {
        var policy = Synthetic.Policy($$"""[ { "path": "Areas", "mutation_requires": "{{Synthetic.ElevatedClass}}" } ]""");
        var authority = new StubAuthority();

        var outcome = Apply(authority, Note("doc.title: Widget"), [Set("doc.title", "Widget")],
            policy: policy, path: "Areas/Agentic/Note.md");

        Assert.Empty(outcome.AssertedAuthority);
        Assert.Empty(authority.Calls);
    }

    // ---- Change detection ----

    [Fact]
    public void TheDiffComparesReEmittedValuesSoAReserializationIsNotAChange()
    {
        // The block's bytes change because the comment and the blank line are lost in the parse,
        // but no protected key's value changes, so a caller holding no class can still write it.
        var authority = new StubAuthority();
        var content = "---\ndoc.owner: someone\n\n# a comment\n---\n" + Body;

        var outcome = Apply(authority, content, [Set("doc.title", "Widget")]);

        Assert.True(outcome.FrontmatterChanged);
        Assert.Equal(["doc.title"], outcome.ChangedKeys);
        Assert.Empty(outcome.AssertedAuthority);
        Assert.Empty(authority.Calls);
    }

    [Fact]
    public void AnOperationThatChangesNothingChangesNoKey()
    {
        var authority = new StubAuthority();

        var outcome = Apply(authority, Note("doc.owner: someone"), [Set("doc.owner", "someone")]);

        Assert.Empty(outcome.ChangedKeys);
        Assert.Empty(authority.Calls);
    }

    [Fact]
    public void RemovingAProtectedKeyRequiresItsAuthority()
    {
        // A removal is a write; without binding it a caller could delete a key it had no authority
        // to create.
        var outcome = Apply(
            new StubAuthority(Synthetic.ElevatedClass), Note("agent.mode: strict\ndoc.title: Widget"),
            [new FrontmatterOperation.RemoveField("agent.mode")]);

        Assert.Equal([Synthetic.ElevatedClass], outcome.AssertedAuthority);
        Assert.Equal(["agent.mode"], outcome.ChangedKeys);
    }

    [Fact]
    public void AnOperationAimedAtAnInnocuousKeyIsClassifiedByWhatItChanged()
    {
        var authority = new StubAuthority();
        var refusal = Assert.Throws<FrontmatterRefusal>(() => Apply(
            authority, Note("agent.instructions:\n  - routed: skill-a"),
            [new FrontmatterOperation.RemoveEntry("agent.instructions", new EntryTarget.Variant("routed"))]));

        Assert.Equal(RefusalReason.AuthorityDenied, refusal.Reason);
        Assert.Equal(["agent.instructions"], refusal.Keys);
    }

    // ---- Assertion ----

    [Fact]
    public void AnEmptyRequiredSetAssertsNothing()
    {
        var authority = new StubAuthority();

        var outcome = Apply(authority, Body, [Set("doc.title", "Widget")]);

        Assert.Empty(outcome.AssertedAuthority);
        Assert.Empty(authority.Calls);
    }

    [Fact]
    public void AnEmptyOpClassSetDenies()
    {
        // Every invocation carries a grant, so holding no class is a denial rather than an
        // absent requirement.
        var refusal = Assert.Throws<FrontmatterRefusal>(() => Apply(new StubAuthority(), Body, [Set("doc.owner", "x")]));

        Assert.Equal(RefusalReason.AuthorityDenied, refusal.Reason);
    }

    [Fact]
    public void ADenialLeavesNothingBehind()
    {
        // Classification runs before anything is serialized, so a denial costs nothing.
        var authority = new StubAuthority();

        Assert.Throws<FrontmatterRefusal>(() => Apply(authority, Body, [Set("agent.mode", "strict")]));
        Assert.Single(authority.Calls);
    }

    // ---- The whole-block assertion ----

    [Fact]
    public void ASuppliedTreeIsAssertedWholeAheadOfTheOutcomeDiff()
    {
        var authority = new StubAuthority(Synthetic.ElevatedClass);

        Apply(authority, string.Empty, suppliedBlock: "agent.mode: strict", newBody: Body);

        // Two assertions: one for the elevated key appearing in the supplied block at all, then
        // one for what the diff found changed.
        Assert.Equal(2, authority.Calls.Count);
        Assert.Equal([Synthetic.ElevatedClass], authority.Calls[0].Required);
        Assert.Equal(["agent.mode"], authority.Calls[0].ChangedKeys);
    }

    [Fact]
    public void TheWholeBlockAssertionIsIndependentOfWhatChanged()
    {
        // The supplied block matches the note exactly, so the diff finds nothing changed, and the
        // registry's assertion still fires because an elevated key was supplied.
        var authority = new StubAuthority();
        var refusal = Assert.Throws<FrontmatterRefusal>(() => Apply(
            authority, Note("agent.mode: strict"), suppliedBlock: "agent.mode: strict", newBody: Body));

        Assert.Equal(RefusalReason.AuthorityDenied, refusal.Reason);
        Assert.Single(authority.Calls);
    }

    // ---- VaultAccess as IKeyAuthority ----

    [Fact]
    public void VaultAccessSatisfiesTheAuthorityInterfaceThroughItsOwnKeyAssertion()
    {
        using var scratch = new ScratchVault();
        IKeyAuthority held = Access(scratch, Synthetic.ElevatedClass);
        IKeyAuthority holdsNothing = Access(scratch);
        var keys = new HashSet<string>(["agent.mode"], StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>([Synthetic.ElevatedClass], StringComparer.Ordinal);

        held.Assert(required, keys);

        var refusal = Assert.Throws<FrontmatterRefusal>(() => holdsNothing.Assert(required, keys));
        Assert.Equal(RefusalReason.AuthorityDenied, refusal.Reason);
        // The refusal names the keys the caller asked for and does not disclose the required class.
        Assert.Equal(["agent.mode"], refusal.Keys);
        Assert.DoesNotContain(Synthetic.ElevatedClass, refusal.Message, StringComparison.Ordinal);
    }

    private static VaultAccess Access(ScratchVault scratch, params string[] opClasses) =>
        new(scratch.Vault, new Grant(
            "test-grant",
            new HashSet<string>(opClasses, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["scratch"], StringComparer.OrdinalIgnoreCase),
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["scratch"] = VaultScope.Unscoped }));
}
