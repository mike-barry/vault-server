using VaultShared.Scope;

namespace VaultShared.Frontmatter;

/// <summary>
/// The one entry point into every write that interprets frontmatter. A consumer reaching the
/// primitives around it would bypass the authority diff, the fence guard, the excluded-path refusal
/// and scalar hygiene, and those are properties of this path rather than of the primitives.
///
/// The phases are fixed and every refusal precedes every change:
/// <list type="number">
///   <item>Validate - the exclusion refusal, the caller-supplied block if there is one, then the
///   per-operation registry checks, scalar hygiene, and declared shape.</item>
///   <item>Apply the operations in order to one working tree, each seeing the previous result,
///   collecting violations for the keys they touched.</item>
///   <item>Classify and assert, over the composed diff, before anything is serialized.</item>
///   <item>Compose, through the single composer where an operation ran and by body splice where
///   none did.</item>
///   <item>Guard - re-split the composed content and require its frontmatter facet to equal what
///   the operations produced, byte for byte.</item>
/// </list>
///
/// <b>Three call shapes run this path with no branch between them.</b> Mutation by operations,
/// composition of a new note from a supplied block and a body, and a narrow pass setting one or two
/// scalar fields are outcomes of the same code rather than modes: composition has no original, so
/// the diff counts every key present as changed, and the narrow pass changes only keys declaring no
/// extra authority, so the required union comes out empty and no assertion fires.
///
/// Two note-dependent refusals are raised in the apply phase rather than the validate one, because
/// operations are ordered and each sees the previous result: whether the note's own field holds a
/// block, and whether an identity or an anchor resolves. Deciding either up front would decide it
/// against a tree an earlier operation has already changed. Both still precede every serialization
/// and every disk change, which is what the ordering of the phases protects.
/// </summary>
public static class FrontmatterWrite
{
    public static WriteOutcome Apply(WriteRequest request)
    {
        var content = NoteSplit.Normalize(request.Content);
        var split = NoteSplit.Of(content);
        var operations = request.Operations;

        // ---- 1. Validate ----

        RefusePathExclusion(request.RelativePath);

        var supplied = request.SuppliedBlock is null ? null : SuppliedTree(request);

        // The note's block is parsed only where the write actually interprets frontmatter. A
        // body-only write neither reads a key nor composes a block, so refusing it over a block
        // nobody could parse would block body edits on exactly the notes that need them; what keeps
        // it from touching frontmatter is the guard, not the parse.
        var interprets = operations.Count > 0 || supplied is not null;
        var original = interprets ? Parse(split) : FrontmatterTree.Empty();
        var working = supplied ?? (interprets ? Parse(split) : FrontmatterTree.Empty());

        for (var i = 0; i < operations.Count; i++)
            Validate(operations[i], i, request.Registry);

        // ---- 2. Apply ----

        var applied = new List<int>();
        var touched = new List<string>();
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            var key = working.Canonicalize(operation.Key, DeclaredSpelling(request.Registry, operation.Key));
            if (Apply(operation, i, key, working, request.Registry))
                applied.Add(i);
            if (!touched.Contains(key, StringComparer.OrdinalIgnoreCase))
                touched.Add(key);
        }

        // Violations cover the keys the operations touched and nothing else: a key the declaration
        // does not recognize, sitting elsewhere in the block, is passed through untouched and not
        // reported. A key an operation removed has nothing left to report on.
        var violations = new List<Violation>();
        foreach (var key in touched.Where(k => working.ShapeOf(k) != FrontmatterShape.Absent))
            violations.AddRange(FrontmatterChecks.CheckKey(request.Registry, working, key));

        // ---- 3. Classify and assert ----

        var body = request.NewBody ?? split.Body;
        var ran = applied.Count > 0 || supplied is not null;
        var mutates = ran || (request.NewBody is not null && request.NewBody != split.Body);

        var changedKeys = interprets ? ChangedKeys(original, working) : Empty;
        var required = Required(request, changedKeys, mutates);
        if (required.Count > 0)
            request.Authority.Assert(required, changedKeys);

        // ---- 4. Compose ----

        // A no-op returns the caller's content unchanged, byte-identical, rather than a
        // reserialization of it: reserialization is confined to writes that actually ran an
        // operation, which is what keeps a duplicate add from reformatting the block it did nothing
        // to.
        if (!mutates)
            return new WriteOutcome(
                request.Content, split, violations, applied,
                FrontmatterChanged: false, BodyChanged: false, changedKeys, required);

        var emitted = ran ? FrontmatterComposer.Emit(working) : string.Empty;
        var composed = ran
            ? FrontmatterComposer.Compose(working, body)
            : string.Concat(content.AsSpan(0, split.BodyOffset), body);

        // ---- 5. Guard ----

        // Prepending a block to a note whose first line already looks like a fence is an ambiguous
        // shape, worse resolved silently than refused. The equality below cannot see it: the
        // composed note re-splits to exactly the block that was authored, with the note's own
        // opener left as the body's first line.
        if (ran && !split.HasBlock && !working.IsEmpty && LooksLikeAnOpeningFence(body))
            throw FrontmatterRefusal.Of(
                RefusalReason.FenceConflict,
                "The note's first line already looks like a fence, so a block prepended to it would be ambiguous.");

        var guard = NoteSplit.Of(composed);
        var expectedBlock = ran ? !working.IsEmpty : split.HasBlock;
        // Emit terminates the block's text; the facet excludes the final inner line's terminator.
        var expectedInner = ran
            ? emitted.Length > 0 ? emitted[..^1] : string.Empty
            : split.InnerText;

        // One equality covers both directions - a write minting a block no operation authored, and
        // one destroying or truncating a block nobody validated - because both come out as the same
        // inequality.
        if (guard.HasBlock != expectedBlock || !string.Equals(guard.InnerText, expectedInner, StringComparison.Ordinal))
            throw FrontmatterRefusal.Of(
                RefusalReason.FenceConflict,
                "The composed note's frontmatter is not what the operations produced.");

        return new WriteOutcome(
            composed,
            guard,
            violations,
            applied,
            FrontmatterChanged: guard.HasBlock != split.HasBlock
                || !string.Equals(guard.InnerText, split.InnerText, StringComparison.Ordinal),
            BodyChanged: !string.Equals(guard.Body, split.Body, StringComparison.Ordinal),
            changedKeys,
            required);
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ---- The exclusion refusal ----

    /// <summary>
    /// The dot-segment rule, which is code rather than declared: nothing under a dot directory is
    /// reachable by this tool set at all. Distinct from an authorization denial - exclusion means
    /// the tooling does not own the file, which no grant changes.
    ///
    /// The path is mandatory, and its absence is an upstream defect rather than a caller error:
    /// path policy matching is its only other use, and an absent path would read as permission on
    /// exactly the files the policy protects.
    /// </summary>
    private static void RefusePathExclusion(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A frontmatter write needs the note's vault-relative path.", nameof(relativePath));

        foreach (var segment in relativePath.Replace('\\', '/').Split('/'))
            if (segment.StartsWith('.'))
                throw FrontmatterRefusal.Of(
                    RefusalReason.PathExcluded, $"\"{relativePath}\" is under a dot segment, which the tooling does not own.");
    }

    // ---- The caller-supplied block ----

    /// <summary>
    /// A caller-supplied block is an input to a parse, never text spliced through: it is reduced to
    /// inner text, parsed, validated whole, and serialized from that tree, so what lands on disk is
    /// what passed validation with no second reading of the caller's bytes to disagree with the
    /// first.
    ///
    /// Its failures are caller errors rather than readings of a file. The caller declared the text
    /// is frontmatter, so an unclosed fence, content after the closing fence, text that will not
    /// parse and text that is not a mapping are all one refusal against the argument.
    /// </summary>
    private static FrontmatterTree SuppliedTree(WriteRequest request)
    {
        var text = NoteSplit.Normalize(request.SuppliedBlock!);
        var inner = text;

        if (LooksLikeAnOpeningFence(text))
        {
            var fenced = NoteSplit.Of(text);
            if (!fenced.HasBlock)
                throw FrontmatterRefusal.Of(
                    RefusalReason.SuppliedBlockInvalid, "The supplied block opens a fence and never closes it.");
            if (!string.IsNullOrWhiteSpace(fenced.Body))
                throw FrontmatterRefusal.Of(
                    RefusalReason.SuppliedBlockInvalid, "The supplied block carries content after its closing fence.");

            inner = fenced.InnerText;
        }

        var parse = FrontmatterTree.TryParse(inner, out var tree);
        if (parse != TreeParse.Parsed)
            throw FrontmatterRefusal.Of(
                RefusalReason.SuppliedBlockInvalid,
                parse == TreeParse.Unparsable
                    ? "The supplied block will not parse as frontmatter."
                    : "The supplied block is not a mapping of keys to values.");

        // Whole-block extent: the caller supplied every key, so all of it is checked, and every
        // finding is a refusal rather than data - a caller can fix what it sent.
        foreach (var key in tree.Keys)
        {
            if (OperationText.HasControlCharacter(tree.Scalar(key) ?? string.Empty))
                throw FrontmatterRefusal.OnKey(
                    RefusalReason.ControlCharacter, key, $"The value of \"{key}\" carries a control character.");

            foreach (var violation in FrontmatterChecks.CheckKey(request.Registry, tree, key))
                throw FrontmatterRefusal.OnKey(Reason(violation.Rule), key, violation.Reasoning);
        }

        // The registry's whole-block assertion, ahead of and independent of the outcome diff: the
        // statement that a key requiring elevation was supplied at all, where the diff remains the
        // authority on what actually changed.
        var keys = new HashSet<string>(tree.Keys, StringComparer.OrdinalIgnoreCase);
        var required = KeyAuthority(request.Registry, keys);
        if (required.Count > 0)
            request.Authority.Assert(required, keys);

        return tree;
    }

    // ---- Per-operation validation ----

    /// <summary>
    /// What is decidable about one operation from the declaration alone: the key is recognized, the
    /// caller's text is clean, the declared shape admits this capability, and an entry payload
    /// satisfies exactly one variant. What the note itself holds is the apply phase's, because each
    /// operation sees the previous one's result.
    /// </summary>
    private static void Validate(FrontmatterOperation operation, int index, FrontmatterRegistry registry)
    {
        if (OperationText.IsEmpty(operation.Key))
            throw FrontmatterRefusal.At(RefusalReason.ValueRefused, index, operation.Key, "The operation names no key.");

        RefuseControlCharacters(operation.Key, operation, index, "the key");

        // The positive allowlist, and the reason it comes first: an undeclared key is refused before
        // its shape matters at all.
        if (!registry.Keys.TryGetValue(operation.Key, out var declaration))
            throw FrontmatterRefusal.At(
                RefusalReason.UnknownKey, index, operation.Key, $"No declaration recognizes the key \"{operation.Key}\".");

        switch (operation)
        {
            case FrontmatterOperation.SetScalar set:
                RefuseControlCharacters(set.Value, operation, index, "the value");
                if (declaration.Shape != KeyShape.Scalar)
                    throw FrontmatterRefusal.At(
                        RefusalReason.ShapeMismatch, index, set.Key,
                        $"\"{set.Key}\" is declared a block list, and a flat scalar operation would replace the list with a string.");
                if (declaration.Value?.Check(set.Value) is { IsSatisfied: false } refused)
                    throw FrontmatterRefusal.At(
                        RefusalReason.ValueRefused, index, set.Key, $"The value of \"{set.Key}\" must be {refused.Wanted}.");
                break;

            case FrontmatterOperation.RemoveField:
                break;

            case FrontmatterOperation.AddEntry add:
                RequireBlockList(declaration, add, index);
                ValidatePayload(add, index, declaration.Entry!);
                if (add.Anchor is { } anchor)
                {
                    RefuseControlCharacters(anchor.Identity, operation, index, "the anchor");
                    if (OperationText.IsEmpty(anchor.Identity))
                        throw FrontmatterRefusal.At(
                            RefusalReason.ValueRefused, index, add.Key, "The anchor names no entry.");
                }
                break;

            case FrontmatterOperation.RemoveEntry remove:
                RequireBlockList(declaration, remove, index);
                ValidateTarget(remove, index, declaration.Entry!);
                break;
        }
    }

    private static void RequireBlockList(KeyDeclaration declaration, FrontmatterOperation operation, int index)
    {
        if (declaration.Shape != KeyShape.BlockList)
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, operation.Key,
                $"\"{operation.Key}\" is declared a scalar, and nothing but a block list holds entries.");
    }

    private static void ValidatePayload(FrontmatterOperation.AddEntry add, int index, EntrySchema schema)
    {
        switch (add.Entry)
        {
            case EntryPayload.Scalar scalar:
                RefuseControlCharacters(scalar.Value, add, index, "the entry");
                if (OperationText.IsEmpty(scalar.Value))
                    throw FrontmatterRefusal.At(RefusalReason.ValueRefused, index, add.Key, "The entry is empty.");
                break;

            case EntryPayload.Mapping mapping:
                foreach (var field in mapping.Fields)
                {
                    RefuseControlCharacters(field.Name, add, index, $"the field name \"{field.Name}\"");
                    RefuseControlCharacters(field.Value, add, index, $"the field \"{field.Name}\"");
                    if (OperationText.IsEmpty(field.Name))
                        throw FrontmatterRefusal.At(RefusalReason.ValueRefused, index, add.Key, "The entry has an unnamed field.");
                }
                break;
        }

        // The same variant matcher and the same field checks the validator runs, so an entry an
        // operation adds is one the validator would pass, and every finding here is a refusal
        // because the caller sent the text.
        foreach (var violation in FrontmatterChecks.CheckEntry(schema, new EntryView(add.Entry), add.Key, 1))
            throw FrontmatterRefusal.At(Reason(violation.Rule), index, add.Key, violation.Reasoning);
    }

    private static void ValidateTarget(FrontmatterOperation.RemoveEntry remove, int index, EntrySchema schema)
    {
        switch (remove.Target)
        {
            case EntryTarget.Identity identity:
                RefuseControlCharacters(identity.Value, remove, index, "the target");
                // The one emptiness test, on the same argument the additive form applies it to.
                if (OperationText.IsEmpty(identity.Value))
                    throw FrontmatterRefusal.At(RefusalReason.ValueRefused, index, remove.Key, "The removal names no entry.");
                break;

            case EntryTarget.Variant named:
                RefuseControlCharacters(named.Name, remove, index, "the variant");
                var variant = schema.Variants.FirstOrDefault(v => string.Equals(v.Name, named.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw FrontmatterRefusal.At(
                        RefusalReason.ValueRefused, index, remove.Key,
                        $"\"{remove.Key}\" declares no variant named \"{named.Name}\".");
                // Whether a removal may clear a whole variant is declared per variant: entries a
                // human authored are dropped one at a time, entries the tooling owns and rewrites
                // are usefully cleared wholesale.
                if (!variant.RemovableWholesale)
                    throw FrontmatterRefusal.At(
                        RefusalReason.WholesaleRemovalRefused, index, remove.Key,
                        $"Variant \"{variant.Name}\" is not clearable wholesale - name its entries one at a time.");
                break;
        }
    }

    private static void RefuseControlCharacters(string text, FrontmatterOperation operation, int index, string what)
    {
        if (OperationText.HasControlCharacter(text))
            throw FrontmatterRefusal.At(
                RefusalReason.ControlCharacter, index, operation.Key, $"On \"{operation.Key}\", {what} carries a control character.");
    }

    // ---- Applying ----

    /// <summary>Applies one operation to the working tree, answering whether it changed
    /// anything.</summary>
    private static bool Apply(
        FrontmatterOperation operation, int index, string key, FrontmatterTree tree, FrontmatterRegistry registry)
    {
        var declaration = registry.Keys[operation.Key];
        var shape = tree.ShapeOf(key);

        switch (operation)
        {
            case FrontmatterOperation.SetScalar set:
                // Detecting the note's own shape first is the only thing between a caller's typo and
                // a destroyed list.
                if (shape == FrontmatterShape.Block)
                    throw FrontmatterRefusal.At(
                        RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds a block in this note, and a scalar set would replace it.");
                if (string.Equals(tree.Scalar(key), set.Value, StringComparison.Ordinal))
                    return false;
                tree.Set(key, set.Value, DeclaredSpelling(registry, operation.Key));
                return true;

            case FrontmatterOperation.RemoveField:
                // The scalar or block path is chosen by the field's detected shape, never by the
                // caller declaring which.
                if (shape == FrontmatterShape.Absent)
                    return false;
                if (shape == FrontmatterShape.Block)
                    RequireClearableWholesale(index, key, tree, declaration);
                return tree.Remove(key);

            case FrontmatterOperation.AddEntry add:
                return Add(add, index, key, tree, declaration, registry);

            case FrontmatterOperation.RemoveEntry remove:
                return Remove(remove, index, key, tree, declaration);

            default:
                throw new InvalidOperationException($"Unhandled frontmatter operation {operation.GetType().Name}.");
        }
    }

    /// <summary>
    /// A block-list field is removable only where every entry under it belongs to a variant the
    /// schema allows clearing wholesale; otherwise the removal is refused rather than applied in
    /// part.
    /// </summary>
    private static void RequireClearableWholesale(int index, string key, FrontmatterTree tree, KeyDeclaration declaration)
    {
        if (declaration.Entry is not { } schema)
            throw FrontmatterRefusal.At(
                RefusalReason.WholesaleRemovalRefused, index, key,
                $"\"{key}\" is declared a scalar and holds a block in this note, so nothing declares its entries clearable.");

        var entries = EntryResolver.Entries(tree, key, schema);
        if (entries.Any(e => e.Variant is not { RemovableWholesale: true }))
            throw FrontmatterRefusal.At(
                RefusalReason.WholesaleRemovalRefused, index, key,
                $"\"{key}\" holds entries that are not clearable wholesale - name them one at a time.");
    }

    private static bool Add(
        FrontmatterOperation.AddEntry add, int index, string key, FrontmatterTree tree,
        KeyDeclaration declaration, FrontmatterRegistry registry)
    {
        var shape = tree.ShapeOf(key);
        if (shape == FrontmatterShape.Scalar)
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds an inline value in this note, and entries need a block list.");

        var schema = declaration.Entry!;
        var entries = EntryResolver.Entries(tree, key, schema);
        var variant = FrontmatterChecks.Match(schema, new EntryView(add.Entry))[0];
        var identity = FrontmatterChecks.IdentityOf(variant, new EntryView(add.Entry));

        if (identity is not null)
        {
            var existing = EntryResolver.Matching(entries, identity);
            if (existing.Count > 1)
                throw FrontmatterRefusal.At(
                    RefusalReason.AmbiguousIdentity, index, key, $"\"{identity}\" already addresses more than one entry under \"{key}\".");
            // Idempotent on declared identity, and a pure no-op: it does not update the entry's
            // other fields. Rewriting an entry is a removal plus an addition in one ordered batch.
            if (existing.Count == 1)
                return false;
        }

        var at = entries.Count;
        if (add.Anchor is { } anchor)
        {
            var anchored = EntryResolver.Matching(entries, anchor.Identity);
            if (anchored.Count == 0)
                throw FrontmatterRefusal.At(
                    RefusalReason.UnresolvedIdentity, index, key, $"The anchor \"{anchor.Identity}\" names no entry under \"{key}\".");
            if (anchored.Count > 1)
                throw FrontmatterRefusal.At(
                    RefusalReason.AmbiguousIdentity, index, key, $"The anchor \"{anchor.Identity}\" names more than one entry under \"{key}\".");

            at = anchored[0].Position + (anchor.Placement == AnchorPlacement.After ? 1 : 0);
        }

        // An add creates its key when absent, and creates the block on a note that has none.
        tree.InsertEntry(key, add.Entry, at, DeclaredSpelling(registry, add.Key));
        return true;
    }

    private static bool Remove(
        FrontmatterOperation.RemoveEntry remove, int index, string key, FrontmatterTree tree, KeyDeclaration declaration)
    {
        var shape = tree.ShapeOf(key);
        if (shape == FrontmatterShape.Absent)
            return false;
        if (shape == FrontmatterShape.Scalar)
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds an inline value in this note, and entries live in a block list.");

        var schema = declaration.Entry!;
        var entries = EntryResolver.Entries(tree, key, schema);

        IReadOnlyList<ResolvedEntry> targeted = remove.Target switch
        {
            EntryTarget.Identity identity => EntryResolver.Matching(entries, identity.Value),
            EntryTarget.Variant named =>
                [.. entries.Where(e => e.Variant is { } v && string.Equals(v.Name, named.Name, StringComparison.OrdinalIgnoreCase))],
            _ => [],
        };

        if (remove.Target is EntryTarget.Identity && targeted.Count > 1)
            throw FrontmatterRefusal.At(
                RefusalReason.AmbiguousIdentity, index, key,
                $"\"{((EntryTarget.Identity)remove.Target).Value}\" addresses more than one entry under \"{key}\".");

        // Removing an absent identity reports no change and mutates nothing - the subtractive half
        // of idempotence, keyed on the same declared identity the additive half uses.
        if (targeted.Count == 0)
            return false;

        // Removing the last entry drops the key rather than leaving it empty.
        tree.RemoveEntriesAt(key, [.. targeted.Select(e => e.Position)]);
        return true;
    }

    // ---- Classification ----

    /// <summary>
    /// Every key whose <em>resulting</em> value differs from its original, comparing values
    /// re-emitted through the composer's own settings rather than raw text - a reserialization that
    /// changed only quoting is not a change and must not deny a write that changed nothing.
    /// </summary>
    private static IReadOnlySet<string> ChangedKeys(FrontmatterTree original, FrontmatterTree result)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in original.Keys.Concat(result.Keys))
        {
            if (changed.Contains(key))
                continue;
            if (!string.Equals(Emitted(original, key), Emitted(result, key), StringComparison.Ordinal))
                changed.Add(key);
        }

        return changed;
    }

    private static string Emitted(FrontmatterTree tree, string key) =>
        FrontmatterComposer.Emit(tree.KeyOnly(key));

    /// <summary>
    /// The union of three contributions, computed over the outcome rather than the request: what the
    /// registry declares for every changed key, what a namespace prefix adds for those same keys,
    /// and what path policy's <c>mutation_requires</c> adds for the note itself - including on a
    /// body-only write, for a file whose body <em>is</em> an agent instruction.
    ///
    /// Classifying by intent would classify by what the caller said it was doing, and every
    /// injection path is a case where an operation aimed at an innocuous field mutates the protected
    /// layer anyway. Measuring the outcome closes the class rather than the instances someone
    /// thought of.
    /// </summary>
    private static IReadOnlySet<string> Required(WriteRequest request, IReadOnlySet<string> changedKeys, bool mutates)
    {
        var required = KeyAuthority(request.Registry, changedKeys);

        // mutation_requires is the only modifier this path reads off the vault's policy; the read
        // floor beside it belongs to scope and never reaches here. A call that mutates nothing
        // mutates no target, so it contributes nothing.
        if (mutates)
            foreach (var opClass in request.Policy.MutationRequires(request.RelativePath))
                required.Add(opClass);

        return required;
    }

    private static HashSet<string> KeyAuthority(FrontmatterRegistry registry, IReadOnlyCollection<string> keys)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (registry.Keys.TryGetValue(key, out var declaration) && declaration.WriteRequires is { } declared)
                required.Add(declared);

            // A namespace match can only add. Nothing is classified as needing no authority by
            // failing to match: a negative match is not evidence.
            foreach (var rule in registry.Namespaces)
                if (key.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                    required.Add(rule.WriteRequires);
        }

        return required;
    }

    // ---- Shared ----

    private static FrontmatterTree Parse(NoteSplit split)
    {
        if (!split.HasBlock)
            return FrontmatterTree.Empty();

        return FrontmatterTree.TryParse(split.InnerText, out var tree) switch
        {
            TreeParse.Parsed => tree,
            TreeParse.NotAMapping => throw FrontmatterRefusal.Of(
                RefusalReason.NotAMapping, "The note's frontmatter block is not a mapping of keys to values."),
            _ => throw FrontmatterRefusal.Of(
                RefusalReason.Unparsable, "The note's frontmatter block will not parse."),
        };
    }

    /// <summary>The declaration's own spelling of a key, for the half of canonicalization that
    /// applies where the note does not already carry it.</summary>
    private static string? DeclaredSpelling(FrontmatterRegistry registry, string key) =>
        registry.Keys.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a text's first line is an opening fence, under the same rule the split
    /// applies: exactly <c>---</c> once trailing whitespace is discounted.</summary>
    private static bool LooksLikeAnOpeningFence(string text)
    {
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).TrimEnd() is "---";
    }

    /// <summary>
    /// What a violation becomes when the text that produced it is the caller's own. The write path
    /// runs the validator's checkers and refuses on their findings; the mapping is here rather than
    /// on the rule, because whether a finding is a refusal is a property of whose text it is.
    /// </summary>
    private static RefusalReason Reason(ViolationRule rule) => rule switch
    {
        ViolationRule.UnknownKey => RefusalReason.UnknownKey,
        ViolationRule.InlineValueWhereBlockListDeclared or ViolationRule.BlockWhereScalarDeclared =>
            RefusalReason.ShapeMismatch,
        _ => RefusalReason.ValueRefused,
    };
}

/// <summary>
/// What a write is. The registry, the policy and the authority arrive as arguments rather than from
/// a global or a container the layer reaches into, which is what lets a conformance suite hand in a
/// synthetic vocabulary and the same code serve both front-ends.
/// </summary>
/// <param name="Content">The note as stored. Empty for a note being composed.</param>
/// <param name="RelativePath">The note's vault-relative path. <b>Mandatory</b>: its only uses are
/// the exclusion refusal and matching path policy, and an absent path would read as permission on
/// exactly the files those protect.</param>
/// <param name="Registry">The vault's loaded declaration. A caller without one has no write to make
/// (<see cref="RegistryState.Require"/>).</param>
/// <param name="Policy">The vault's path policy. <see cref="PathPolicy.None"/> for a vault that
/// declares nothing.</param>
/// <param name="Authority">What asserts the required op classes once the outcome is known.</param>
/// <param name="Operations">The operations, applied in order, each seeing the previous result.
/// Repetition is meaningful.</param>
/// <param name="SuppliedBlock">A whole block the caller declares, with or without its fences.
/// Validated whole, because the caller supplied every key.</param>
/// <param name="NewBody">A replacement body, or null to keep the note's own.</param>
public sealed record WriteRequest(
    string Content,
    string RelativePath,
    FrontmatterRegistry Registry,
    PathPolicy Policy,
    IKeyAuthority Authority,
    IReadOnlyList<FrontmatterOperation> Operations,
    string? SuppliedBlock,
    string? NewBody)
{
    public IReadOnlyList<FrontmatterOperation> Operations { get; } = Operations ?? [];
}

/// <summary>
/// What a write turned out to be. It carries the composed content, both facets, the violation list,
/// which operations applied, whether each facet changed, which keys changed, and what write
/// authority the outcome required.
/// </summary>
/// <param name="Content">The composed note. Byte-identical to what the caller sent where nothing
/// changed.</param>
/// <param name="Split">Both facets of <paramref name="Content"/>, from the one definition of the
/// split, so what a tool echoes back is byte-identical to what a reader returns.</param>
/// <param name="Violations">Schema violations under the keys the operations touched, as data. A
/// single-note tool treats a non-empty list as failure and a sweeping tool renders findings; the
/// path does not decide for either.</param>
/// <param name="AppliedOperations">The indices, into the request's own operation list, of the
/// operations that changed something. 0-based, because it indexes a list the caller holds.</param>
/// <param name="FrontmatterChanged">Whether the frontmatter facet's bytes differ from the note's.
/// A reserialization that changed only quoting is a byte change and not a
/// <paramref name="ChangedKeys"/> one.</param>
/// <param name="BodyChanged">Whether the body facet's bytes differ from the note's.</param>
/// <param name="ChangedKeys">Every key whose re-emitted value differs from its original.</param>
/// <param name="AssertedAuthority">The union the outcome required, empty where it required
/// nothing.</param>
public sealed record WriteOutcome(
    string Content,
    NoteSplit Split,
    IReadOnlyList<Violation> Violations,
    IReadOnlyList<int> AppliedOperations,
    bool FrontmatterChanged,
    bool BodyChanged,
    IReadOnlySet<string> ChangedKeys,
    IReadOnlySet<string> AssertedAuthority);

/// <summary>
/// Asserting the authority a write turned out to require. One member and one job, so the write path
/// holds what asserts rather than what a vault is.
///
/// Returning the requirement for a caller to assert instead would make the assertion optional at
/// every call site and the guarantee a convention. The per-call access object satisfies this, which
/// is how the mid-operation key assertion reaches this path without a vault type entering the
/// namespace.
/// </summary>
public interface IKeyAuthority
{
    /// <summary>
    /// Throws where <paramref name="required"/> is not held. An empty set asserts nothing, and the
    /// path does not call this with one.
    /// </summary>
    /// <param name="required">The op classes the outcome requires, as a union.</param>
    /// <param name="changedKeys">The call's own coordinates, for a denial to name. It discloses
    /// nothing about what the caller holds.</param>
    void Assert(IReadOnlySet<string> required, IReadOnlySet<string> changedKeys);
}
