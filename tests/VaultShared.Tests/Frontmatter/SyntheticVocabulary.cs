using System.Text.Json;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Provides the synthetic frontmatter declaration the write path, validator, and vocabulary
/// projection tests run against, along with helpers for building write requests. The keys are
/// invented rather than taken from any real vault, because what is under test is the mechanism.
/// </summary>
/// <remarks>
/// The registry is built directly from records rather than parsed from JSON, so these tests turn
/// on the write path alone. Loading a declaration from a file is covered by
/// <see cref="FrontmatterRegistryLoaderTests"/>.
/// </remarks>
internal static class Synthetic
{
    internal const string OpenClass = "content:write";
    internal const string ElevatedClass = "agentic:write";

    /// <summary>Gets a scalar variant with no discriminator and therefore no identity.</summary>
    internal static EntryVariant Hint { get; } = new(
        "hint", null, new StringConstraint(), Fields(), [], [], StringComparison.Ordinal, RemovableWholesale: false);

    /// <summary>Gets a variant whose identity is matched case-sensitively and cannot be cleared
    /// wholesale.</summary>
    internal static EntryVariant Pinned { get; } = new(
        "pinned", "pinned", null,
        Fields(("pinned", true, new StringConstraint())),
        [], ["pinned"], StringComparison.Ordinal, RemovableWholesale: false);

    /// <summary>Gets a variant whose identity is matched case-insensitively and that may be cleared
    /// wholesale.</summary>
    internal static EntryVariant Routed { get; } = new(
        "routed", "routed", null,
        Fields(("routed", true, new StringConstraint()), ("when", false, new StringConstraint())),
        [], ["routed"], StringComparison.OrdinalIgnoreCase, RemovableWholesale: true);

    /// <summary>
    /// Gets a variant with a required field that is not its discriminator, which separates the
    /// missing-required-field check from the no-matching-variant check.
    /// </summary>
    internal static EntryVariant Cited { get; } = new(
        "cited", "cited", null,
        Fields(("cited", true, new StringConstraint()), ("source", true, new StringConstraint())),
        [], ["cited"], StringComparison.Ordinal, RemovableWholesale: false);

    /// <summary>Gets a variant whose exclusive group is also its identity.</summary>
    internal static EntryVariant Link { get; } = new(
        "link", "label", null,
        Fields(("label", true, new StringConstraint()), ("link", false, new WikilinkConstraint()), ("folder", false, new StringConstraint())),
        ["link", "folder"], ["link", "folder"], StringComparison.OrdinalIgnoreCase, RemovableWholesale: false);

    internal static EntrySchema DirectiveSchema { get; } = new([Hint, Pinned, Routed, Cited]);

    internal static EntrySchema LinkSchema { get; } = new([Link]);

    /// <summary>
    /// Gets the declaration these tests run against. Two block-list keys share one schema instance;
    /// one scalar key declares its own operation class, one falls under a namespace that declares
    /// one, and the rest declare none.
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
    /// Builds a path policy for a single vault through the same parser the configuration loader
    /// uses, so this suite touches that signature in one place.
    /// </summary>
    /// <param name="entries">The JSON array of path policy entries.</param>
    /// <returns>The parsed policy.</returns>
    internal static PathPolicy Policy(string entries)
    {
        using var doc = JsonDocument.Parse(entries);
        var sink = new RecordingProblemSink();
        var policy = PathPolicyParser.Parse(
            doc.RootElement, "synthetic", TestVaultFixture.RepoRoot, "vaults.json", TestOpClasses.Repo, sink);
        return policy;
    }

    /// <summary>
    /// Builds a write request over <paramref name="content"/> with everything else defaulted, so a
    /// test names only the parts it is about.
    /// </summary>
    /// <param name="content">The note's current content.</param>
    /// <param name="authority">The authority the write asserts against.</param>
    /// <param name="operations">The operations to apply, or null for none.</param>
    /// <param name="suppliedBlock">A whole frontmatter block supplied by the caller, or null.</param>
    /// <param name="newBody">The replacement body, or null to leave the body alone.</param>
    /// <param name="path">The vault-relative path of the note.</param>
    /// <param name="policy">The path policy to apply, or null for an empty policy.</param>
    /// <param name="registry">The declaration to validate against, or null for <see cref="Registry"/>.</param>
    /// <returns>The write request.</returns>
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
/// Records the authority assertions a write makes, so a test can check that none fired, and denies
/// by throwing the same refusal <c>VaultAccess</c> throws, naming the changed key set.
/// </summary>
/// <param name="held">The operation classes this authority holds.</param>
internal sealed class StubAuthority(params string[] held) : IKeyAuthority
{
    /// <summary>
    /// Gets the assertions made against this authority, in order. It stays empty when the required
    /// set came out empty, because the write path does not assert an empty requirement.
    /// </summary>
    internal List<(IReadOnlySet<string> Required, IReadOnlySet<string> ChangedKeys)> Calls { get; } = [];

    /// <summary>Gets the operation classes this authority holds.</summary>
    internal IReadOnlySet<string> Held { get; } = new HashSet<string>(held, StringComparer.Ordinal);

    /// <summary>Records the assertion and denies it unless every required class is held.</summary>
    /// <param name="required">The operation classes the write requires.</param>
    /// <param name="changedKeys">The keys the write changes.</param>
    /// <exception cref="FrontmatterRefusal">A required class is not held.</exception>
    public void Assert(IReadOnlySet<string> required, IReadOnlySet<string> changedKeys)
    {
        Calls.Add((required, changedKeys));
        if (required.Any(opClass => !Held.Contains(opClass)))
            throw FrontmatterRefusal.Denied(changedKeys);
    }
}
