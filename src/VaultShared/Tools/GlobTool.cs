using System.ComponentModel;
using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>
/// Represents <see cref="GlobTool"/>'s arguments. <see cref="Pattern"/> carries no default and is
/// therefore the one required member.
/// </summary>
/// <param name="Pattern">The glob pattern to match.</param>
/// <param name="Vault">The vault to search, or null for the configured default.</param>
public sealed record GlobArguments(
    [property: Description("Glob pattern, matched case-insensitively, with \"**\" for recursive descent. A pattern containing no separator matches a path's final segment at any depth; one containing a separator is anchored at the vault root.")]
    string Pattern,
    [property: Description("The vault to search in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null) : IVaultArguments
{
    /// <summary>Checks that a pattern was supplied.</summary>
    /// <returns>A fault when the pattern is missing, otherwise null. Whether the pattern is
    /// well-formed is the search engine's answer and comes back as <c>invalid_pattern</c>; this code
    /// does not own that grammar and must not re-decide it.</returns>
    public ArgumentFault? Validate() => ArgumentRules.Missing(Pattern, "pattern");
}

/// <summary>
/// Finds notes by name and path. This is the only search over names and paths.
/// </summary>
/// <remarks>
/// It enumerates notes rather than every file in the vault, because its subject is a note. Searching
/// all files would return attachments that no tool here can operate on.
///
/// Any number of matches, including none, is a result. No match returns empty text and never a
/// failure, so an existence check does not force a caller to catch, and a caller that needs exactly
/// one match enforces that itself.
/// </remarks>
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

        // The caller names a pattern rather than a path, so nothing is refused for scope here. The
        // access object filters the results against the effective read scope. It also applies the
        // markdown restriction as a predicate over what the engine returned. Never append ".md" to
        // the caller's pattern instead: "notes/**" becomes "notes/**.md", which the engine accepts
        // and treats as a single "*", silently dropping every nested note.
        var outcome = vault.EnumerateNotes(arguments.Pattern);
        if (outcome is not SearchOutcome<VaultPath>.Results results)
            throw ToolRefusalEnvelope.Translate(outcome);

        return new ToolResult.Content(string.Join("\n", results.Items.Select(path => path.Value)));
    }
}
