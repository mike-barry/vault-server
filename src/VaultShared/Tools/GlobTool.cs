using System.ComponentModel;
using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>
/// <see cref="GlobTool"/>'s arguments. <see cref="Pattern"/> carries no default, so it is the one
/// required member: a discovery call naming no pattern is asking for the whole vault, which is a
/// listing rather than a search and is better spelled explicitly.
/// </summary>
public sealed record GlobArguments(
    [property: Description("Glob pattern, matched case-insensitively, with \"**\" for recursive descent. A pattern containing no separator matches a path's final segment at any depth; one containing a separator is anchored at the vault root.")]
    string Pattern,
    [property: Description("The vault to search in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null) : IVaultArguments
{
    /// <summary>
    /// Presence, and nothing more. The pattern's well-formedness is the search engine's answer and
    /// arrives as <c>invalid_pattern</c>; re-deciding it here would be a second reading of a grammar
    /// this build does not own.
    /// </summary>
    public ArgumentFault? Validate() => ArgumentRules.Missing(Pattern, "pattern");
}

/// <summary>
/// The only name-and-path search surface, and the discovery step that turns a name into a path.
///
/// It asks note enumeration, because its subject is a note: the markdown domain is a semantic choice
/// rather than a shared-code convenience, and asking the census instead would return attachments no
/// tool here can operate on.
///
/// As a search tool it treats any count as a result. No match is empty text and never a failure - an
/// existence check must not require a caller to catch, and a caller needing exactly one enforces
/// that itself.
/// </summary>
public sealed class GlobTool : ITool
{
    public const string ToolName = "glob";

    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Finds notes by name and path. Returns matching vault-relative paths, one per line, and " +
            "empty output when nothing matched. Use it to turn a name into the path every other tool takes.",
        Arguments = typeof(GlobArguments),
        OpClassFloor = new Floor.Class("content:read"),
    };

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<GlobArguments>();
        var vault = call.RequireVault(ToolName);

        // No path is named here, so nothing is gated loudly: the pattern is a pattern, and what it
        // reaches is filtered against the effective read scope by the access object itself. The
        // markdown restriction is likewise the layer's predicate over what the engine returned and
        // is never appended to this pattern - "notes/**" with ".md" appended is "notes/**.md", which
        // the engine accepts and degrades to a single "*", silently dropping every nested note.
        var outcome = vault.EnumerateNotes(arguments.Pattern);
        if (outcome is not SearchOutcome<VaultPath>.Results results)
            throw ToolRefusalEnvelope.Translate(outcome);

        return new ToolResult.Content(string.Join("\n", results.Items.Select(path => path.Value)));
    }
}
