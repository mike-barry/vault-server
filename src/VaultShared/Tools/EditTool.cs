using System.Text.Json;
using System.Text.Json.Nodes;
using VaultShared.Access;
using VaultShared.Frontmatter;

// Two namespaces each declare a WriteOutcome: the access layer's answer to storing bytes, and the
// frontmatter write path's answer to composing a note. This alias names the first; the second is
// only ever held in a var.
using WriteOutcome = VaultShared.Access.WriteOutcome;

namespace VaultShared.Tools;

/// <summary>
/// Represents the result of an edit operation. Every member is one this tool genuinely populates.
/// </summary>
/// <param name="ContentHash">The note's content hash after the write, so a chain of edits needs no
/// read in between. It is the same value a read of the file returns.</param>
/// <param name="FrontmatterChanged">Whether the note's frontmatter block differs from what it
/// carried.</param>
/// <param name="BodyChanged">Whether the note's body differs from what it carried.</param>
/// <param name="ChangedKeys">Every key whose value differs from what the note carried, compared as
/// re-emitted values, so a reserialization that changed only quoting is not a changed key.</param>
/// <param name="AppliedOperations">The indices, into the caller's own operation list, of the
/// operations that changed something. An operation that changed nothing is absent.</param>
/// <param name="AssertedAuthority">The op classes the outcome required beyond the tool's floor. It is
/// reported even when empty, so that a caller can tell an operation that asserted nothing from a tool
/// that never asserts.</param>
/// <remarks>
/// There is deliberately no member reporting schema violations, and an edit never refuses over one.
/// Everything a caller can get wrong is already refused outright, so a violation still standing in
/// the outcome was in the note before the call. Refusing over it would make a list unaddable because
/// someone else broke an entry in it.
/// </remarks>
public sealed record EditResult(
    string ContentHash,
    bool FrontmatterChanged,
    bool BodyChanged,
    IReadOnlyList<string> ChangedKeys,
    IReadOnlyList<int> AppliedOperations,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// Edits one existing note: ordered frontmatter operations plus at most one body change, applied as
/// a single outcome. This is the only way to mutate an existing note.
/// </summary>
/// <remarks>
/// <para>
/// The note must exist, and a miss is refused. This tool creates nothing and relocates nothing;
/// existence, content and location are three separate tools, and a tool that created on a miss would
/// turn a typo into a new note.
/// </para>
/// <para>
/// The tool's own steps run after the shared execute path has validated the arguments and resolved
/// the vault: path resolution, the staleness check, the substitution's own anchor, then the single
/// frontmatter write path, which classifies and asserts the authority the outcome turned out to
/// require, and only then the write. Every refusal therefore precedes every change.
/// </para>
/// <para>
/// The read-modify-write in the middle is guarded twice: by the write lock the execute path holds
/// around the whole invocation, and by the content hash, which covers the window the lock cannot -
/// the time between the caller's read and its edit, during which a person or an editor can change
/// the file.
/// </para>
/// <para>
/// There is no preview. An edit touches only the note it names, so a plan would just echo the request
/// back, and previews that say nothing train callers to skip the ones that matter.
/// </para>
/// </remarks>
/// <param name="registries">Every vault's loaded frontmatter declaration. Held on the tool rather
/// than passed per call, because a declaration is per vault and loaded once at startup, and a call
/// carries a vault rather than a vocabulary.</param>
public sealed class EditTool(FrontmatterRegistries registries) : ITool, IVocabularySchema
{
    /// <summary>The tool's wire name.</summary>
    public const string ToolName = "edit";

    /// <summary>
    /// The tool's declaration. The floor is the base write class and is not the whole authority: what
    /// a particular edit requires depends on which keys it turned out to change and on what the
    /// vault's path policy says about the note. Both are asserted inside the write path, against the
    /// outcome rather than against what the caller said it was doing.
    /// </summary>
    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Edits one existing note: ordered frontmatter operations, plus at most one body change, " +
            "applied together or not at all. The path is vault-relative and carries the \".md\" " +
            "extension. Replacing the body wholesale requires the content hash from a read; a " +
            "find-and-replace must match exactly once. Returns the note's new content hash, which " +
            "chains straight into the next edit.",
        Arguments = typeof(EditArguments),
        OpClassFloor = new Floor.Class("content:write"),
        Mutates = true,
    };

    /// <summary>
    /// Builds the advertised schema for one vault's entry vocabulary: one branch per entry variant,
    /// with each variant's discriminating field required on its own branch.
    /// </summary>
    /// <param name="vocabulary">The vault's declared entry variants.</param>
    /// <returns>The argument schema with the <c>entry</c> node constrained to the declared variants,
    /// or the plain argument schema where nothing is declared.</returns>
    /// <remarks>
    /// This is a function rather than a member of the declaration because a tool is declared once
    /// while the entry vocabulary is per vault, and a tool listing is not per vault. The projection
    /// chooses which vault's vocabulary to advertise and calls this.
    ///
    /// Where nothing is declared, the branches are simply absent and <c>entry</c> stays
    /// unconstrained. Advertising is not permission: the write path refuses anything the declaration
    /// does not recognize either way.
    /// </remarks>
    public static JsonElement Schema(FrontmatterVocabulary vocabulary)
    {
        var schema = JsonNode.Parse(NativeToolArguments.SchemaFor(typeof(EditArguments)).GetRawText())!;
        if (vocabulary.Branches.Count == 0 || Locate(schema) is not { } entry)
            return JsonSerializer.SerializeToElement(schema);

        // anyOf rather than oneOf. Two declarations that happen to describe the same shape would each
        // match a payload, and an exclusive alternation would advertise that as invalid. Matching a
        // variant is the write path's job, which it does by walking the declaration once.
        entry["anyOf"] = new JsonArray([.. vocabulary.Branches.Select(Branch)]);
        entry.Remove("description");
        entry["description"] =
            "The entry to add: an object matching one of the variants below, or a bare value where a "
            + "variant declares one. Which variants exist is this vault's own declaration.";

        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>Returns one vault's declared entry vocabulary.</summary>
    /// <param name="vaultName">The vault whose declaration to read.</param>
    /// <returns>The vocabulary, or the empty one where nothing loaded.</returns>
    public FrontmatterVocabulary Vocabulary(string vaultName) =>
        FrontmatterVocabulary.Of(registries.For(vaultName));

    /// <inheritdoc/>
    public JsonElement AdvertisedSchema(string vaultName) => Schema(Vocabulary(vaultName));

    /// <inheritdoc/>
    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<EditArguments>();
        var vault = call.RequireVault(ToolName);

        // Argument validation and vault resolution have already run in the shared execute path.

        // Path resolution, scope, and policy.
        var path = ToolPaths.WriteNote(vault, arguments.Path);
        var stored = Read(path, arguments.Path);

        // Plan.
        var operations = arguments.Ordered.Select(o => o.Decode()).ToList();
        // A body-only edit is not a frontmatter write: it reads no key, and what determines its
        // authority is the vault's path policy. So it runs against the vocabulary that interprets
        // nothing, rather than refusing over a declaration its body has nothing to do with.
        var registry = operations.Count > 0
            ? registries.For(vault.Vault.Name).Require()
            : MutationSupport.NoVocabulary;

        RefuseIfStale(arguments, stored);
        var body = NewBody(arguments, stored.Text);

        // The single frontmatter write path. It validates every operation against the declaration,
        // applies them in order, classifies the authority the outcome requires over the composed
        // diff, and asserts it through the access object - all before anything is serialized, so a
        // denial costs nothing and leaves nothing half-done. This tool catches none of its refusals;
        // they reach the wire through ToolRefusalEnvelope.
        var outcome = FrontmatterWrite.Apply(new WriteRequest(
            stored.Text,
            path.Path.Value,
            registry,
            vault.Vault.Policy,
            vault,
            operations,
            SuppliedBlock: null,
            NewBody: body));

        // Apply.
        return new ToolResult.Data(new EditResult(
            Store(path, arguments.Path, outcome.Content, stored.ContentHash),
            outcome.FrontmatterChanged,
            outcome.BodyChanged,
            [.. outcome.ChangedKeys.Order(StringComparer.Ordinal)],
            outcome.AppliedOperations,
            [.. outcome.AssertedAuthority.Order(StringComparer.Ordinal)]));
    }

    /// <summary>Reads the note the edit targets.</summary>
    /// <param name="path">The admitted write path.</param>
    /// <param name="named">The path as the caller spelled it, used in refusal messages.</param>
    /// <returns>The note's stored text and content hash.</returns>
    /// <exception cref="ToolRefusal">The note is not there, or could not be read.</exception>
    private static ReadOutcome.Content Read(WritePath path, string named) =>
        VaultFiles.Read(path.AsRead()) switch
        {
            ReadOutcome.Content stored => stored,
            ReadOutcome.NotFound => throw new ToolRefusal(
                ToolCodes.PathNotFound,
                $"No note is at \"{named}\". Editing changes a note that exists; bringing one into existence is create's."),
            ReadOutcome.Failed failed => throw new ToolRefusal(
                ToolCodes.ReadFailed,
                $"\"{named}\" is there and could not be read: {failed.Reason}",
                new { path = failed.Path }),
            var other => throw new InvalidOperationException($"Unhandled read outcome {other.GetType().Name}."),
        };

    /// <summary>
    /// Refuses a wholesale body replacement whose content hash no longer matches the file. This
    /// guards the one operation that can destroy content the caller never saw.
    /// </summary>
    /// <param name="arguments">The validated arguments, carrying the caller's expected hash.</param>
    /// <param name="stored">The note as it was just read.</param>
    /// <exception cref="ToolRefusal">The file changed since the caller took its hash.</exception>
    /// <remarks>
    /// The hash is compared against the file's stored bytes, which is what makes the hash a read
    /// returns and the hash a write returns the same value.
    ///
    /// The refusal carries no payload. Returning the current hash would let a caller retry with it
    /// and overwrite content it never read; the only recovery is to read the note again.
    /// </remarks>
    private static void RefuseIfStale(EditArguments arguments, ReadOutcome.Content stored)
    {
        if (arguments.ExpectedContentHash is not { } expected)
            return;

        if (!string.Equals(expected.Trim(), stored.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new ToolRefusal(
                ToolCodes.ContentChanged,
                $"\"{arguments.Path}\" has changed since the hash was taken. Read it again and compose the replacement against what it says now.");
    }

    /// <summary>
    /// Composes the new body a write should store.
    /// </summary>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="content">The note's whole stored text.</param>
    /// <returns>The caller's text for a wholesale replacement, the body with its single occurrence
    /// substituted for a substitution, or null to keep the note's own body.</returns>
    /// <exception cref="ToolRefusal">The anchor text matched no times or more than once.</exception>
    /// <remarks>
    /// Matching exactly once is how a substitution verifies that it is editing what the caller meant,
    /// so no match and several matches are each refused rather than partly applied. There is
    /// deliberately no way to spell a replace-every-occurrence call.
    /// </remarks>
    private static string? NewBody(EditArguments arguments, string content)
    {
        if (arguments.Body is not { } change)
            return null;
        if (change.Content is { } wholesale)
            return NoteSplit.Normalize(wholesale);

        // Match against the same normalized text the write path composes from, so an anchor the
        // caller copied out of a read matches the bytes the write actually holds.
        var body = NoteSplit.Of(content).Body;
        var find = NoteSplit.Normalize(change.Find!);

        var count = Occurrences(body, find);
        if (count == 0)
            throw new ToolRefusal(
                ToolCodes.BodyMatchNotFound,
                "The text to substitute does not occur in the note's body.");
        if (count > 1)
            throw new ToolRefusal(
                ToolCodes.BodyMatchAmbiguous,
                $"The text to substitute occurs {count} times in the note's body, and a substitution matches exactly once. Send more surrounding text.",
                new { count });

        var at = body.IndexOf(find, StringComparison.Ordinal);
        return string.Concat(body.AsSpan(0, at), NoteSplit.Normalize(change.Replace!), body.AsSpan(at + find.Length));
    }

    /// <summary>Counts non-overlapping occurrences, which is what "exactly once" means here.</summary>
    /// <param name="text">The text to search.</param>
    /// <param name="find">The text to count.</param>
    /// <returns>The number of non-overlapping occurrences.</returns>
    private static int Occurrences(string text, string find)
    {
        var count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    /// <summary>
    /// Stores the composed note and returns the hash of what is on disk afterwards.
    /// </summary>
    /// <param name="path">The admitted write path.</param>
    /// <param name="named">The path as the caller spelled it, used in refusal messages.</param>
    /// <param name="content">The composed note text.</param>
    /// <param name="storedHash">The hash of the note as it was read.</param>
    /// <returns>The file's content hash after the call.</returns>
    /// <exception cref="ToolRefusal">The note vanished, its parent vanished, or the write
    /// failed.</exception>
    /// <remarks>
    /// A write that would store the bytes already on disk is skipped. The two hashes are equal
    /// exactly when the write would change nothing, so an operation that turned out to be a no-op
    /// does not disturb the file's timestamp, and the value returned is the same either way.
    /// </remarks>
    private static string Store(WritePath path, string named, string content, string storedHash)
    {
        var composed = VaultFiles.DigestOf(content);
        if (string.Equals(composed, storedHash, StringComparison.Ordinal))
            return storedHash;

        return VaultFiles.Replace(path, content) switch
        {
            WriteOutcome.Written written => written.ContentHash!,

            // The note was read a moment ago under the vault's write lock, so each of these is an
            // operational failure rather than a caller error: something outside this process moved
            // or removed the file while the write was in flight.
            WriteOutcome.NotFound => throw new ToolRefusal(
                ToolCodes.PathNotFound,
                $"\"{named}\" was there when it was read and is gone now. Nothing was written."),
            WriteOutcome.MissingParent missing => throw new ToolRefusal(
                ToolCodes.ParentMissing,
                $"\"{named}\" cannot be written because \"{missing.Parent}\" is not there. Nothing was written.",
                new { parent = missing.Parent }),
            WriteOutcome.Failed failed => throw new ToolRefusal(
                ToolCodes.WriteFailed,
                $"\"{named}\" could not be written: {failed.Reason}",
                new { path = failed.Path }),
            var other => throw new InvalidOperationException($"Unhandled write outcome {other.GetType().Name}."),
        };
    }

    /// <summary>Finds the <c>entry</c> node inside the derived schema, wherever the exporter put the
    /// operation record: inline, or behind a definition it referenced.</summary>
    /// <param name="schema">The derived argument schema.</param>
    /// <returns>The <c>entry</c> node, or null when it is not there.</returns>
    private static JsonObject? Locate(JsonNode schema)
    {
        foreach (var node in Walk(schema))
            if (node["properties"] is JsonObject properties
                && properties.ContainsKey("op")
                && properties["entry"] is JsonObject entry)
                return entry;

        return null;
    }

    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var (_, child) in o)
                    foreach (var found in Walk(child))
                        yield return found;
                break;

            case JsonArray a:
                foreach (var child in a)
                    foreach (var found in Walk(child))
                        yield return found;
                break;
        }
    }

    /// <summary>
    /// Renders one declared variant as one branch of the alternation.
    /// </summary>
    /// <param name="branch">The declared variant.</param>
    /// <returns>The branch schema.</returns>
    /// <remarks>
    /// Variants are told apart by which fields are present rather than by a field's value, so a
    /// branch expresses that by listing its discriminating field as required. Anything that has no
    /// clean expression in the schema goes into the branch's description instead.
    /// </remarks>
    private static JsonNode Branch(EntryBranch branch)
    {
        if (branch.Scalar is { } scalar)
        {
            var bare = new JsonObject { ["type"] = scalar.Type, ["title"] = branch.Variant };
            Describe(bare, branch.Description, scalar.Description, Accepted(branch));
            if (scalar.Enum is { } values)
                bare["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
            return bare;
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in branch.Fields)
        {
            var node = new JsonObject { ["type"] = field.Value.Type };
            if (field.Value.Enum is { } values)
                node["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
            Describe(node, field.Description, field.Value.Description);

            properties[field.Name] = node;
            if (field.Required)
                required.Add(field.Name);
        }

        var mapping = new JsonObject
        {
            ["type"] = "object",
            ["title"] = branch.Variant,
            ["properties"] = properties,
        };
        if (required.Count > 0)
            mapping["required"] = required;
        Describe(mapping, branch.Description, Accepted(branch));

        return mapping;
    }

    /// <summary>Describes which declared keys accept this branch. A branch belongs to an entry schema
    /// rather than to a key, and several keys can name the same schema, so the branch names its keys
    /// instead of being restated under each of them.</summary>
    /// <param name="branch">The declared variant.</param>
    /// <returns>A sentence for the branch's description.</returns>
    private static string Accepted(EntryBranch branch) =>
        $"Accepted under {string.Join(", ", branch.Keys.Select(k => $"\"{k}\""))}.";

    /// <summary>Joins the non-empty parts into the node's <c>description</c>, leaving it absent when
    /// there is nothing to say.</summary>
    /// <param name="node">The schema node to describe.</param>
    /// <param name="parts">Description fragments, in order.</param>
    private static void Describe(JsonObject node, params string?[] parts)
    {
        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (text.Length > 0)
            node["description"] = text;
    }
}
