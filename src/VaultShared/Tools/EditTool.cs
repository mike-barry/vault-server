using System.Text.Json;
using System.Text.Json.Nodes;
using VaultShared.Access;
using VaultShared.Frontmatter;

// Both namespaces carry a WriteOutcome - the layer's answer to storing bytes, and the write path's
// answer to composing a note. Only the first is named here; the second arrives through var.
using WriteOutcome = VaultShared.Access.WriteOutcome;

namespace VaultShared.Tools;

/// <summary>
/// What an edit turned out to be. Every member is one this tool genuinely populates: uniformity
/// across the mutation results is an outcome rather than a goal, and a member forced onto a tool
/// that does not do that thing is a claim it never made.
///
/// <b>No violations member, and no refusal on one.</b> Everything a caller can get wrong is already
/// a hard refusal, so a schema violation still standing in the outcome was in the note before the
/// call - refusing over it would make a list unaddable because somebody else broke an entry in it.
/// </summary>
/// <param name="ContentHash">The note's hash after the write, so a chain of edits needs no
/// interleaved read. The value a read of the same file answers.</param>
/// <param name="ChangedKeys">Every key whose value differs from what the note carried, compared as
/// re-emitted values - a reserialization that changed only quoting is not a changed key.</param>
/// <param name="AppliedOperations">The indices, into the caller's own operation list, of the
/// operations that changed something. An operation that was a no-op is simply absent.</param>
/// <param name="AssertedAuthority">What the outcome required beyond the tool's floor. It stays when
/// empty: absence would not distinguish an operation that asserted nothing from a tool that never
/// asserts.</param>
public sealed record EditResult(
    string ContentHash,
    bool FrontmatterChanged,
    bool BodyChanged,
    IReadOnlyList<string> ChangedKeys,
    IReadOnlyList<int> AppliedOperations,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// The only way to mutate an existing note: ordered frontmatter operations plus at most one body
/// change, applied as a single outcome.
///
/// It operates on an existing target, so it requires exactly one and fails on a miss. It creates
/// nothing and relocates nothing - existence, content and location are three separate tools, and a
/// tool that created on a miss would turn a typo into a new note.
///
/// <b>Its own five phases, after the execute path's six.</b> Path resolution, then staleness, then
/// the substitution's own anchor, then the single frontmatter write path - which classifies and
/// asserts the authority the outcome turned out to require - and only then the write. Every refusal
/// therefore precedes every change, and the read-modify-write in the middle is guarded twice over:
/// by the mutex the execute path holds around the whole invocation, and by the staleness hash, which
/// covers the window the lock cannot - model time, during which the editor or a person can change
/// the file.
///
/// <b>There is no preview gate.</b> An edit's blast radius is its own request: it touches the one
/// note it names, and a plan would echo the request back, which trains callers to skip previews
/// generally.
/// </summary>
/// <param name="registries">Every vault's loaded declaration. Held rather than handed in on the
/// call, because the declaration is per vault and loaded once at startup, and the call carries a
/// vault rather than a vocabulary.</param>
public sealed class EditTool(FrontmatterRegistries registries) : ITool, IVocabularySchema
{
    public const string ToolName = "edit";

    /// <summary>
    /// The floor is the base write class, and it is not the whole authority: what a particular edit
    /// requires depends on which keys it turned out to change and on what the vault's path policy
    /// says about the note, and both are asserted inside the write path on the outcome rather than
    /// on what the caller said it was doing.
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
    /// The advertised entry vocabulary for one vault's declaration: one branch per entry variant,
    /// each variant's discriminating field pinned on its own branch, generated from the same
    /// registry instance the write path holds.
    ///
    /// <b>This is the schema seam, and it is a function rather than a member of the declaration.</b>
    /// A tool is declared once and its argument record is the source of its advertised schema, but
    /// the entry branch is per vault, and <c>tools/list</c> is not - so which vocabulary a listing
    /// carries is the projection's choice and this is what it calls. Where nothing loaded, the
    /// branches are simply absent and <c>entry</c> stays unconstrained: advertising was never
    /// permission, and the write path refuses whatever the declaration does not recognize.
    /// </summary>
    public static JsonElement Schema(FrontmatterVocabulary vocabulary)
    {
        var schema = JsonNode.Parse(NativeToolArguments.SchemaFor(typeof(EditArguments)).GetRawText())!;
        if (vocabulary.Branches.Count == 0 || Locate(schema) is not { } entry)
            return JsonSerializer.SerializeToElement(schema);

        // anyOf rather than oneOf: two keys naming one schema hold one object and produce one
        // branch, but two declarations that happen to describe the same shape would each match a
        // payload, and an exclusive alternation would advertise that as invalid. The write path
        // matches a variant by walking the declaration once, and that remains the enforcement.
        entry["anyOf"] = new JsonArray([.. vocabulary.Branches.Select(Branch)]);
        entry.Remove("description");
        entry["description"] =
            "The entry to add: an object matching one of the variants below, or a bare value where a "
            + "variant declares one. Which variants exist is this vault's own declaration.";

        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>The advertised vocabulary of one vault, or the empty one where nothing loaded.</summary>
    public FrontmatterVocabulary Vocabulary(string vaultName) =>
        FrontmatterVocabulary.Of(registries.For(vaultName));

    /// <summary>
    /// The seam a front-end reaches this tool's schema through, and the reason it needs no per-tool
    /// branch to do it: the projection names a vault and this composes the two halves.
    /// </summary>
    public JsonElement AdvertisedSchema(string vaultName) => Schema(Vocabulary(vaultName));

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<EditArguments>();
        var vault = call.RequireVault(ToolName);

        // ---- 3. Path resolution, scope, and policy ----

        var path = ToolPaths.WriteNote(vault, arguments.Path);
        var stored = Read(path, arguments.Path);

        // ---- 4. Plan ----

        var operations = arguments.Ordered.Select(o => o.Decode()).ToList();
        // A body-only edit is not a frontmatter write - it reads no key, and what prices it is the
        // vault's path policy - so it runs against the vocabulary that interprets nothing rather
        // than refusing on a declaration its body has nothing to do with.
        var registry = operations.Count > 0
            ? registries.For(vault.Vault.Name).Require()
            : MutationSupport.NoVocabulary;

        RefuseIfStale(arguments, stored);
        var body = NewBody(arguments, stored.Text);

        // The single write path: it validates every operation against the declaration, applies them
        // in order, classifies the authority the outcome requires over the composed diff, and
        // asserts it through the access object - all before anything is serialized, so a denial
        // costs nothing and leaves nothing half-done. Its refusals reach the wire through the one
        // table that names them; this tool catches none of them.
        var outcome = FrontmatterWrite.Apply(new WriteRequest(
            stored.Text,
            path.Path.Value,
            registry,
            vault.Vault.Policy,
            vault,
            operations,
            SuppliedBlock: null,
            NewBody: body));

        // ---- 5. Apply ----

        return new ToolResult.Data(new EditResult(
            Store(path, arguments.Path, outcome.Content, stored.ContentHash),
            outcome.FrontmatterChanged,
            outcome.BodyChanged,
            [.. outcome.ChangedKeys.Order(StringComparer.Ordinal)],
            outcome.AppliedOperations,
            [.. outcome.AssertedAuthority.Order(StringComparer.Ordinal)]));
    }

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
    /// The staleness guard, on the one operation that can destroy content the caller never saw.
    /// The hash is compared against the file's stored bytes, which is what makes a hash from a read
    /// and a hash from a mutation the same value.
    ///
    /// <b>The refusal carries no payload.</b> Returning the current hash would let a caller retry
    /// with it and clobber content it never read; recovery is a re-read.
    /// </summary>
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
    /// The body a write composes, or null to keep the note's own. A wholesale replacement is the
    /// caller's text; a substitution is the note's body with exactly one occurrence replaced.
    ///
    /// <b>Exactly once is the self-verification of a surgical edit</b>, which is why zero matches
    /// and several are each a refusal rather than a partial application, and why no spelling of this
    /// call replaces every occurrence.
    /// </summary>
    private static string? NewBody(EditArguments arguments, string content)
    {
        if (arguments.Body is not { } change)
            return null;
        if (change.Content is { } wholesale)
            return NoteSplit.Normalize(wholesale);

        // Against the same normalized text the write path will compose from, so an anchor a caller
        // took out of a read matches the bytes the write actually holds.
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

    /// <summary>Non-overlapping occurrences, which is what "exactly once" counts.</summary>
    private static int Occurrences(string text, string find)
    {
        var count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    /// <summary>
    /// Stores the composed note and answers the hash of what is on disk afterwards.
    ///
    /// A write that would store the bytes already there is skipped rather than performed. The two
    /// hashes are equal exactly when the write would change nothing, so the file's mtime is not
    /// disturbed by an operation that turned out to be a no-op, and the value answered is the same
    /// either way.
    /// </summary>
    private static string Store(WritePath path, string named, string content, string storedHash)
    {
        var composed = VaultFiles.DigestOf(content);
        if (string.Equals(composed, storedHash, StringComparison.Ordinal))
            return storedHash;

        return VaultFiles.Replace(path, content) switch
        {
            WriteOutcome.Written written => written.ContentHash!,

            // The note was read a moment ago under the vault's write lock, so every one of these is
            // operational rather than a caller error - something outside this build moved or took
            // the file while the write was in flight.
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

    /// <summary>The <c>entry</c> node inside the derived schema, wherever the exporter put the
    /// operation record - inline or behind a definition it referenced.</summary>
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
    /// One variant, as one branch of the alternation. Discrimination is by a field's presence rather
    /// than by its value, so the discriminating field is pinned by being required on its branch -
    /// which is what presence-based discrimination becomes in a client-facing schema. What has no
    /// clean expression there rides the description the projection already carries.
    /// </summary>
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

    /// <summary>Which declared keys take this branch. A branch belongs to a schema rather than to a
    /// key, and two keys naming one schema hold one object - so the branch says which keys accept
    /// it rather than being restated under each.</summary>
    private static string Accepted(EntryBranch branch) =>
        $"Accepted under {string.Join(", ", branch.Keys.Select(k => $"\"{k}\""))}.";

    private static void Describe(JsonObject node, params string?[] parts)
    {
        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (text.Length > 0)
            node["description"] = text;
    }
}
