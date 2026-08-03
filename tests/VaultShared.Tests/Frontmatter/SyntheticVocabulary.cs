using System.Text.Json;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The synthetic declaration the write path, the validator and the vocabulary projection are
/// asserted against. Conformance posture: what is under test is the mechanism, so the keys below are
/// invented and none of them is the vault's. A declaration the vault really carries would prove
/// nothing this does not, and asserting against one would make the suite a copy of a file it does
/// not own.
///
/// It is built as records rather than parsed from JSON so these cases turn on the write path alone;
/// what a file becomes is <c>FrontmatterRegistryLoaderTests</c>'s subject.
/// </summary>
internal static class Synthetic
{
    internal const string OpenClass = "content:write";
    internal const string ElevatedClass = "agentic:write";

    /// <summary>A bare value: the revisable hint, with no discriminator and so no identity.</summary>
    internal static EntryVariant Hint { get; } = new(
        "hint", null, new StringConstraint(), Fields(), [], [], StringComparison.Ordinal, RemovableWholesale: false);

    /// <summary>A user-authored rule: matched exactly, removed one at a time.</summary>
    internal static EntryVariant Pinned { get; } = new(
        "pinned", "pinned", null,
        Fields(("pinned", true, new StringConstraint())),
        [], ["pinned"], StringComparison.Ordinal, RemovableWholesale: false);

    /// <summary>Tooling-owned routing: matched case-insensitively, clearable wholesale.</summary>
    internal static EntryVariant Routed { get; } = new(
        "routed", "routed", null,
        Fields(("routed", true, new StringConstraint()), ("when", false, new StringConstraint())),
        [], ["routed"], StringComparison.OrdinalIgnoreCase, RemovableWholesale: true);

    /// <summary>A variant whose required field is not its discriminator, so the two checks are
    /// tellable apart.</summary>
    internal static EntryVariant Cited { get; } = new(
        "cited", "cited", null,
        Fields(("cited", true, new StringConstraint()), ("source", true, new StringConstraint())),
        [], ["cited"], StringComparison.Ordinal, RemovableWholesale: false);

    /// <summary>One variant with an exclusive group that is also its identity.</summary>
    internal static EntryVariant Link { get; } = new(
        "link", "label", null,
        Fields(("label", true, new StringConstraint()), ("link", false, new WikilinkConstraint()), ("folder", false, new StringConstraint())),
        ["link", "folder"], ["link", "folder"], StringComparison.OrdinalIgnoreCase, RemovableWholesale: false);

    internal static EntrySchema DirectiveSchema { get; } = new([Hint, Pinned, Routed, Cited]);

    internal static EntrySchema LinkSchema { get; } = new([Link]);

    /// <summary>
    /// Two block-list keys share one schema instance, as two keys naming one schema do; one scalar
    /// key declares its own authority, one falls under a namespace that declares it, and the rest
    /// declare none.
    /// </summary>
    internal static FrontmatterRegistry Registry { get; } = new(
        new Dictionary<string, KeyDeclaration>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc.title"] = new(KeyShape.Scalar, new StringConstraint(), null, null),
            ["doc.type"] = new(KeyShape.Scalar, new CharClassConstraint(CharacterClass.AlphaDash, 1, 20), null, null),
            ["doc.created"] = new(KeyShape.Scalar, new DateConstraint(DateConstraint.IsoDate), null, null),
            ["doc.owner"] = new(KeyShape.Scalar, new StringConstraint(), null, OpenClass),
            ["agent.mode"] = new(KeyShape.Scalar, new StringConstraint(), null, null),
            ["nav.contents"] = new(KeyShape.BlockList, null, LinkSchema, null),
            ["nav.see_also"] = new(KeyShape.BlockList, null, LinkSchema, null),
            ["doc.notes"] = new(KeyShape.BlockList, null, DirectiveSchema, null),
            ["agent.instructions"] = new(KeyShape.BlockList, null, DirectiveSchema, null),
        },
        [new NamespaceRule("agent.", ElevatedClass)],
        new Dictionary<string, EntrySchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["directive_entry"] = DirectiveSchema,
            ["link_entry"] = LinkSchema,
        });

    private static Dictionary<string, EntryField> Fields(params (string Name, bool Required, ValueConstraint Value)[] fields)
    {
        var map = new Dictionary<string, EntryField>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, required, value) in fields)
            map[name] = new EntryField(required, value);

        return map;
    }

    /// <summary>
    /// A path policy over one vault, built through its only producer. One site, so the parser's
    /// signature is a single point of contact for this suite.
    /// </summary>
    internal static PathPolicy Policy(string entries)
    {
        using var doc = JsonDocument.Parse(entries);
        var sink = new RecordingProblemSink();
        var policy = PathPolicyParser.Parse(
            doc.RootElement, "synthetic", TestVaultFixture.RepoRoot, "vaults.json", TestOpClasses.Repo, sink);
        return policy;
    }

    /// <summary>A write request over <paramref name="content"/> with everything else defaulted, so a
    /// case names only what it is about.</summary>
    internal static WriteRequest Write(
        string content,
        IKeyAuthority authority,
        IReadOnlyList<FrontmatterOperation>? operations = null,
        string? suppliedBlock = null,
        string? newBody = null,
        string path = "Notes/Note.md",
        PathPolicy? policy = null,
        FrontmatterRegistry? registry = null) =>
        new(content, path, registry ?? Registry, policy ?? PathPolicy.None, authority, operations ?? [], suppliedBlock, newBody);
}

/// <summary>
/// What the write path asserts against. It records every assertion, so a case can say that none
/// fired, and it denies in the surface's own vocabulary, naming the changed key set exactly as
/// <c>VaultAccess</c> does.
/// </summary>
internal sealed class StubAuthority(params string[] held) : IKeyAuthority
{
    /// <summary>Every assertion, in order. Empty where the required union came out empty - the path
    /// does not assert nothing.</summary>
    internal List<(IReadOnlySet<string> Required, IReadOnlySet<string> ChangedKeys)> Calls { get; } = [];

    internal IReadOnlySet<string> Held { get; } = new HashSet<string>(held, StringComparer.Ordinal);

    public void Assert(IReadOnlySet<string> required, IReadOnlySet<string> changedKeys)
    {
        Calls.Add((required, changedKeys));
        if (required.Any(opClass => !Held.Contains(opClass)))
            throw FrontmatterRefusal.Denied(changedKeys);
    }
}
