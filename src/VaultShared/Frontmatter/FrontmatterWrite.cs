using VaultShared.Scope;

namespace VaultShared.Frontmatter;

/// <summary>
/// Applies every write that interprets frontmatter. This is the only entry point. A consumer
/// calling the primitives around it would bypass the authority diff, the fence guard, the
/// excluded-path refusal and text hygiene, all of which belong to this path rather than to the
/// primitives.
///
/// The phases run in a fixed order, and every refusal precedes every change:
/// <list type="number">
///   <item>Validate - the exclusion refusal, the caller-supplied block if there is one, then the
///   per-operation registry checks, text hygiene, and declared shape.</item>
///   <item>Apply the operations in order to one working tree, each seeing the previous result,
///   collecting violations for the keys they touched.</item>
///   <item>Classify and assert authority, over the diff, before anything is serialized.</item>
///   <item>Compose, through the composer where an operation ran and by body splice where none
///   did.</item>
///   <item>Guard - re-split the composed content and require its frontmatter to equal what the
///   operations produced, byte for byte.</item>
/// </list>
///
/// Three call shapes run this same path with no branch between them: mutation by operations,
/// composition of a new note from a supplied block and a body, and a narrow pass setting one or two
/// scalar fields. Composition has no original, so the diff counts every key present as changed. The
/// narrow pass changes only keys declaring no extra authority, so the required union comes out
/// empty and no assertion fires.
///
/// Two note-dependent refusals are raised in the apply phase rather than the validate phase:
/// whether the note's own key holds a block, and whether an identity or an anchor resolves.
/// Operations are ordered and each sees the previous result, so deciding either up front would
/// decide it against a tree an earlier operation has already changed. Both still precede every
/// serialization and every disk change.
/// </summary>
public static class FrontmatterWrite
{
    /// <summary>Applies a write and returns what it turned out to be.</summary>
    /// <param name="request">The note, the operations, and everything the path needs to decide.</param>
    /// <returns>The composed content and what changed. A write that changes nothing returns the
    /// caller's content byte-identically.</returns>
    /// <exception cref="FrontmatterRefusal">The write was refused. See <see cref="RefusalReason"/>
    /// for the distinctions.</exception>
    /// <exception cref="ArgumentException">The request carries no vault-relative path.</exception>
    public static WriteOutcome Apply(WriteRequest request)
    {
        var content = NoteSplit.Normalize(request.Content);
        var split = NoteSplit.Of(content);
        var operations = request.Operations;

        // ---- Validate ----

        RefusePathExclusion(request.RelativePath);

        var supplied = request.SuppliedBlock is null ? null : SuppliedTree(request);

        // The note's block is parsed only where the write actually interprets frontmatter. A
        // body-only write neither reads a key nor composes a block, so refusing it over a block
        // nobody could parse would block body edits on exactly the notes that need them. What keeps
        // a body-only write off the frontmatter is the guard, not the parse.
        var interprets = operations.Count > 0 || supplied is not null;
        var original = interprets ? Parse(split) : FrontmatterTree.Empty();
        var working = supplied ?? (interprets ? Parse(split) : FrontmatterTree.Empty());

        for (var i = 0; i < operations.Count; i++)
            Validate(operations[i], i, request.Registry);

        // ---- Apply ----

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

        // Violations cover the keys the operations touched and nothing else. An undeclared key
        // sitting elsewhere in the block is passed through untouched and not reported, and a key an
        // operation removed has nothing left to report on.
        var violations = new List<Violation>();
        foreach (var key in touched.Where(k => working.ShapeOf(k) != FrontmatterShape.Absent))
            violations.AddRange(FrontmatterChecks.CheckKey(request.Registry, working, key));

        // ---- Classify and assert authority ----

        var body = request.NewBody ?? split.Body;
        var ran = applied.Count > 0 || supplied is not null;
        var mutates = ran || (request.NewBody is not null && request.NewBody != split.Body);

        var changedKeys = interprets ? ChangedKeys(original, working) : Empty;
        var required = Required(request, changedKeys, mutates);
        if (required.Count > 0)
            request.Authority.Assert(required, changedKeys);

        // ---- Compose ----

        // A write that changed nothing returns the caller's content byte-identically rather than a
        // reserialization of it. Reserialization happens only where an operation actually ran, so a
        // duplicate add cannot reformat the block it did nothing to.
        if (!mutates)
            return new WriteOutcome(
                request.Content, split, violations, applied,
                FrontmatterChanged: false, BodyChanged: false, changedKeys, required);

        var emitted = ran ? FrontmatterComposer.Emit(working) : string.Empty;
        var composed = ran
            ? FrontmatterComposer.Compose(working, body)
            : string.Concat(content.AsSpan(0, split.BodyOffset), body);

        // ---- Guard ----

        // Prepending a block to a note whose first line already looks like a fence is ambiguous,
        // and is refused rather than resolved silently. The equality check below cannot catch it,
        // because the composed note re-splits to exactly the block that was authored with the
        // note's own opener left as the body's first line.
        if (ran && !split.HasBlock && !working.IsEmpty && LooksLikeAnOpeningFence(body))
            throw FrontmatterRefusal.Of(
                RefusalReason.FenceConflict,
                "The note's first line already looks like a fence, so a block prepended to it would be ambiguous.");

        var guard = NoteSplit.Of(composed);
        var expectedBlock = ran ? !working.IsEmpty : split.HasBlock;
        // Emit terminates the block's text, and the split's inner text excludes the final inner
        // line's terminator.
        var expectedInner = ran
            ? emitted.Length > 0 ? emitted[..^1] : string.Empty
            : split.InnerText;

        // One equality covers both directions: a write that minted a block no operation authored,
        // and one that destroyed or truncated a block nobody validated.
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
    /// Refuses a note under a dot segment. The rule is in code rather than declared, because nothing
    /// under a dot directory is reachable by this tool set at all. It is distinct from an
    /// authorization denial, because exclusion means the tooling does not own the file and no grant
    /// changes that.
    /// </summary>
    /// <param name="relativePath">The note's vault-relative path. Mandatory. Its only uses are this
    /// refusal and matching path policy, so an absent path would read as permission on exactly the
    /// files those protect.</param>
    /// <exception cref="ArgumentException">The path is missing, which is an upstream defect rather
    /// than a caller error.</exception>
    /// <exception cref="FrontmatterRefusal">The path is under a dot segment.</exception>
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
    /// Parses and validates a caller-supplied block. The text is an input to a parse and is never
    /// spliced through: it is reduced to inner text, parsed, validated whole, and serialized from
    /// that tree, so what lands on disk is what passed validation and no second reading of the
    /// caller's bytes can disagree with the first.
    ///
    /// Failures here are caller errors rather than defects in a file. The caller declared the text
    /// was frontmatter, so an unclosed fence, content after the closing fence, text that will not
    /// parse and text that is not a mapping all refuse against the argument.
    /// </summary>
    /// <param name="request">The write request, whose supplied block is parsed and whose registry
    /// and authority validate it.</param>
    /// <returns>The parsed and validated block.</returns>
    /// <exception cref="FrontmatterRefusal">The supplied block does not hold up as frontmatter,
    /// carries a control character, violates the declaration, or requires authority the caller does
    /// not hold.</exception>
    private static FrontmatterTree SuppliedTree(WriteRequest request)
    {
        var text = NoteSplit.Normalize(request.SuppliedBlock!);
        var inner = text;

        // How many lines of the caller's text the inner region starts past. A parse position counts
        // lines of the inner text, and the caller counts lines of what it sent, so a position from
        // a fenced block that was not shifted would name the line above the mistake.
        var strippedLines = 0;

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

            // The opening fence is the first line or there is no block at all, so it is exactly one
            // line, and the fence occupies a whole line so no column moves.
            strippedLines = 1;
        }

        var parse = FrontmatterTree.TryParse(inner, out var tree, out var failedAt);
        // The position reaches the caller as structure rather than as message text, which keeps the
        // message a fixed string and leaves the caller a line and a column to correct instead of a
        // whole block to regenerate. Which failure this is decides the message; whether a position
        // came with it decides only the payload.
        if (parse == TreeParse.Unparsable)
            throw failedAt is { } position
                ? FrontmatterRefusal.AtPosition(
                    RefusalReason.SuppliedBlockInvalid,
                    position with { Line = position.Line + strippedLines },
                    "The supplied block will not parse as frontmatter.")
                : FrontmatterRefusal.Of(
                    RefusalReason.SuppliedBlockInvalid, "The supplied block will not parse as frontmatter.");

        if (parse != TreeParse.Parsed)
            throw FrontmatterRefusal.Of(
                RefusalReason.SuppliedBlockInvalid, "The supplied block is not a mapping of keys to values.");

        // The caller supplied every key, so every key is checked and every finding refuses rather
        // than being reported as data. A caller can fix what it sent.
        foreach (var key in tree.Keys)
        {
            RefuseControlCharacters(tree, key);

            foreach (var violation in FrontmatterChecks.CheckKey(request.Registry, tree, key))
            {
                // An undeclared key refuses the same way here as it does per operation, payload
                // included, so a caller does not meet one unknown_key that names the declared keys
                // and another that does not.
                var reason = Reason(violation.Rule);
                throw reason == RefusalReason.UnknownKey
                    ? FrontmatterRefusal.Undeclared(key, DeclaredKeys(request.Registry), null, violation.Reasoning)
                    : FrontmatterRefusal.OnKey(reason, key, violation.Reasoning);
            }
        }

        // Supplying a key that requires elevation is itself asserted here, ahead of and
        // independently of the outcome diff, which remains what decides whether the key changed.
        var keys = new HashSet<string>(tree.Keys, StringComparer.OrdinalIgnoreCase);
        var required = KeyAuthority(request.Registry, keys);
        if (required.Count > 0)
            request.Authority.Assert(required, keys);

        return tree;
    }

    /// <summary>
    /// Refuses a supplied key carrying a control character anywhere under it: the key's own value,
    /// the text of a bare entry in its block list, and every field value of every mapping entry
    /// there.
    /// </summary>
    /// <param name="tree">The caller-supplied block.</param>
    /// <param name="key">The key whose values are screened, named in the refusal.</param>
    /// <exception cref="FrontmatterRefusal">Some value under the key carries a control
    /// character.</exception>
    /// <remarks>
    /// The screen reaches the entries rather than the key's own value alone. A terminal escape is a
    /// control character, the most agent-facing text a note carries lives in its block-list entries,
    /// and a supplied block is the route that admits the most caller text in one call. The screen
    /// refuses rather than strips, because silently rewriting a value reports success while changing
    /// what was asked for.
    /// </remarks>
    private static void RefuseControlCharacters(FrontmatterTree tree, string key)
    {
        var carries = OperationText.HasControlCharacter(tree.Scalar(key) ?? string.Empty);
        foreach (var entry in tree.Entries(key))
        {
            carries |= OperationText.HasControlCharacter(entry.Value ?? string.Empty);
            foreach (var field in entry.Fields)
                carries |= OperationText.HasControlCharacter(entry.Field(field) ?? string.Empty);
        }

        if (carries)
            throw FrontmatterRefusal.OnKey(
                RefusalReason.ControlCharacter, key, $"The value of \"{key}\" carries a control character.");
    }

    // ---- Per-operation validation ----

    /// <summary>
    /// Checks what the declaration alone can decide about one operation: the key is recognized, the
    /// caller's text is clean, the declared shape admits this operation, and an entry payload
    /// satisfies exactly one variant. What the note itself holds is left to the apply phase,
    /// because each operation sees the previous one's result.
    /// </summary>
    /// <param name="operation">The operation to check.</param>
    /// <param name="index">Its index in the request's operation list, carried onto any refusal.</param>
    /// <param name="registry">The vault's loaded declaration.</param>
    /// <exception cref="FrontmatterRefusal">The operation is refused.</exception>
    private static void Validate(FrontmatterOperation operation, int index, FrontmatterRegistry registry)
    {
        if (OperationText.IsEmpty(operation.Key))
            throw FrontmatterRefusal.At(RefusalReason.ValueRefused, index, operation.Key, "The operation names no key.");

        RefuseControlCharacters(operation.Key, operation, index, "the key");

        // The allowlist comes first, so an undeclared key is refused before its shape matters.
        if (!registry.Keys.TryGetValue(operation.Key, out var declaration))
            throw FrontmatterRefusal.Undeclared(
                operation.Key, DeclaredKeys(registry), index, $"No declaration recognizes the key \"{operation.Key}\".");

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

    /// <summary>Refuses an entry operation against a key the declaration does not call a block list.</summary>
    /// <param name="declaration">The key's declaration.</param>
    /// <param name="operation">The operation being validated.</param>
    /// <param name="index">Its index in the request's operation list.</param>
    /// <exception cref="FrontmatterRefusal">The key is declared a scalar.</exception>
    private static void RequireBlockList(KeyDeclaration declaration, FrontmatterOperation operation, int index)
    {
        if (declaration.Shape != KeyShape.BlockList)
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, operation.Key,
                $"\"{operation.Key}\" is declared a scalar, and nothing but a block list holds entries.");
    }

    /// <summary>Checks an added entry's text and its fit against the schema.</summary>
    /// <param name="add">The add operation carrying the payload.</param>
    /// <param name="index">Its index in the request's operation list.</param>
    /// <param name="schema">The schema the entry must satisfy.</param>
    /// <exception cref="FrontmatterRefusal">The payload carries a control character, is empty, has
    /// an unnamed field or a field with no value, or does not satisfy exactly one variant.</exception>
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
                    // Every field's value is tested for emptiness, not only a required one's, where
                    // empty means null, empty, or whitespace only. The asymmetry that follows is
                    // accepted: a note may already hold an entry with an empty optional field and
                    // validate clean, while an operation adding that same entry is refused. Refusing
                    // is the safe direction, since an entry with no identity cannot be named by a
                    // later removal.
                    if (OperationText.IsEmpty(field.Value))
                        throw FrontmatterRefusal.At(
                            RefusalReason.ValueRefused, index, add.Key, $"The field \"{field.Name}\" has no value.");
                }
                break;
        }

        // The same variant matcher and field checks the validator runs, so an entry an operation
        // adds is one the validator would pass. Every finding refuses here because the caller sent
        // the text.
        foreach (var violation in FrontmatterChecks.CheckEntry(schema, new EntryView(add.Entry), add.Key, 1))
            throw FrontmatterRefusal.At(Reason(violation.Rule), index, add.Key, violation.Reasoning);
    }

    /// <summary>Checks what a removal addresses.</summary>
    /// <param name="remove">The removal operation carrying the target.</param>
    /// <param name="index">Its index in the request's operation list.</param>
    /// <param name="schema">The schema the target is resolved against.</param>
    /// <exception cref="FrontmatterRefusal">The target carries a control character, names no entry,
    /// names a variant the schema does not declare, or names a variant that is not clearable
    /// wholesale.</exception>
    private static void ValidateTarget(FrontmatterOperation.RemoveEntry remove, int index, EntrySchema schema)
    {
        switch (remove.Target)
        {
            case EntryTarget.Identity identity:
                RefuseControlCharacters(identity.Value, remove, index, "the target");
                // The same emptiness test the additive form applies to the same argument.
                if (OperationText.IsEmpty(identity.Value))
                    throw FrontmatterRefusal.At(RefusalReason.ValueRefused, index, remove.Key, "The removal names no entry.");
                break;

            case EntryTarget.Variant named:
                RefuseControlCharacters(named.Name, remove, index, "the variant");
                var variant = schema.Variants.FirstOrDefault(v => string.Equals(v.Name, named.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw FrontmatterRefusal.At(
                        RefusalReason.ValueRefused, index, remove.Key,
                        $"\"{remove.Key}\" declares no variant named \"{named.Name}\".");
                if (!variant.RemovableWholesale)
                    throw FrontmatterRefusal.At(
                        RefusalReason.WholesaleRemovalRefused, index, remove.Key,
                        $"Variant \"{variant.Name}\" is not clearable wholesale - name its entries one at a time.");
                break;
        }
    }

    /// <summary>Refuses caller text carrying a control character.</summary>
    /// <param name="text">The caller text to test.</param>
    /// <param name="operation">The operation the text came from.</param>
    /// <param name="index">Its index in the request's operation list.</param>
    /// <param name="what">What the text is, named in the refusal message.</param>
    /// <exception cref="FrontmatterRefusal">The text carries a control character.</exception>
    private static void RefuseControlCharacters(string text, FrontmatterOperation operation, int index, string what)
    {
        if (OperationText.HasControlCharacter(text))
            throw FrontmatterRefusal.At(
                RefusalReason.ControlCharacter, index, operation.Key, $"On \"{operation.Key}\", {what} carries a control character.");
    }

    // ---- Applying ----

    /// <summary>Applies one operation to the working tree.</summary>
    /// <param name="operation">The operation to apply.</param>
    /// <param name="index">Its index in the request's operation list, carried onto any refusal.</param>
    /// <param name="key">The canonical spelling of the key it names.</param>
    /// <param name="tree">The working tree, mutated in place.</param>
    /// <param name="registry">The vault's loaded declaration.</param>
    /// <returns>True when the operation changed something.</returns>
    /// <exception cref="FrontmatterRefusal">The note's own shape or its entries refuse the
    /// operation.</exception>
    private static bool Apply(
        FrontmatterOperation operation, int index, string key, FrontmatterTree tree, FrontmatterRegistry registry)
    {
        var declaration = registry.Keys[operation.Key];
        var shape = tree.ShapeOf(key);

        switch (operation)
        {
            case FrontmatterOperation.SetScalar set:
                // Checking the note's own shape first is the only thing between a caller's typo and
                // a destroyed list.
                if (shape == FrontmatterShape.Block)
                    throw FrontmatterRefusal.At(
                        RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds a block in this note, and a scalar set would replace it.");
                if (string.Equals(tree.Scalar(key), set.Value, StringComparison.Ordinal))
                    return false;
                tree.Set(key, set.Value, DeclaredSpelling(registry, operation.Key));
                return true;

            case FrontmatterOperation.RemoveField:
                // The scalar or block path is chosen from the key's detected shape, never from the
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
    /// Refuses removing a block-list key unless every entry under it belongs to a variant the
    /// schema allows clearing wholesale. The removal is refused rather than applied in part.
    /// </summary>
    /// <param name="index">The operation's index in the request's operation list.</param>
    /// <param name="key">The key being removed.</param>
    /// <param name="tree">The working tree holding the entries.</param>
    /// <param name="declaration">The key's declaration.</param>
    /// <exception cref="FrontmatterRefusal">The key declares no entry schema, holds a nested mapping
    /// rather than a block list, or holds an entry that is not clearable wholesale.</exception>
    private static void RequireClearableWholesale(int index, string key, FrontmatterTree tree, KeyDeclaration declaration)
    {
        if (declaration.Entry is not { } schema)
            throw FrontmatterRefusal.At(
                RefusalReason.WholesaleRemovalRefused, index, key,
                $"\"{key}\" is declared a scalar and holds a block in this note, so nothing declares its entries clearable.");

        // A nested mapping is a block and is not a block list, and the check below resolves no
        // entries from one - so it would pass vacuously and the removal would delete a structure no
        // schema was consulted about. The shape is refused ahead of the resolution rather than after
        // it.
        if (!tree.HoldsBlockList(key))
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key,
                $"\"{key}\" holds a nested mapping in this note rather than a block list, and nothing declares what such a structure contains.");

        var entries = EntryResolver.Entries(tree, key, schema);
        if (entries.Any(e => e.Variant is not { RemovableWholesale: true }))
            throw FrontmatterRefusal.At(
                RefusalReason.WholesaleRemovalRefused, index, key,
                $"\"{key}\" holds entries that are not clearable wholesale - name them one at a time.");
    }

    /// <summary>Adds an entry to a block list.</summary>
    /// <param name="add">The operation to apply.</param>
    /// <param name="index">Its index in the request's operation list.</param>
    /// <param name="key">The canonical spelling of the block-list key.</param>
    /// <param name="tree">The working tree, mutated in place.</param>
    /// <param name="declaration">The key's declaration.</param>
    /// <param name="registry">The vault's loaded declaration, for the key's declared spelling.</param>
    /// <returns>True when an entry was inserted, and false where an entry with the same identity
    /// was already there.</returns>
    /// <exception cref="FrontmatterRefusal">The note's key holds an inline value, the identity
    /// already addresses more than one entry, or the anchor resolves to no entry or to
    /// several.</exception>
    private static bool Add(
        FrontmatterOperation.AddEntry add, int index, string key, FrontmatterTree tree,
        KeyDeclaration declaration, FrontmatterRegistry registry)
    {
        var shape = tree.ShapeOf(key);
        if (shape == FrontmatterShape.Scalar)
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds an inline value in this note, and entries need a block list.");
        if (shape != FrontmatterShape.Absent && !tree.HoldsBlockList(key))
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds a nested mapping in this note, and entries need a block list.");

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
            // An add whose identity is already present is a no-op and does not update the entry's
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

    /// <summary>Removes entries from a block list.</summary>
    /// <param name="remove">The operation to apply.</param>
    /// <param name="index">Its index in the request's operation list.</param>
    /// <param name="key">The canonical spelling of the block-list key.</param>
    /// <param name="tree">The working tree, mutated in place.</param>
    /// <param name="declaration">The key's declaration.</param>
    /// <returns>True when an entry was removed, and false where the key is absent or the target
    /// matched nothing.</returns>
    /// <exception cref="FrontmatterRefusal">The note's key holds an inline value, or an identity
    /// addresses more than one entry.</exception>
    private static bool Remove(
        FrontmatterOperation.RemoveEntry remove, int index, string key, FrontmatterTree tree, KeyDeclaration declaration)
    {
        var shape = tree.ShapeOf(key);
        if (shape == FrontmatterShape.Absent)
            return false;
        if (shape == FrontmatterShape.Scalar)
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds an inline value in this note, and entries live in a block list.");
        if (!tree.HoldsBlockList(key))
            throw FrontmatterRefusal.At(
                RefusalReason.ShapeMismatch, index, key, $"\"{key}\" holds a nested mapping in this note, and entries live in a block list.");

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

        // Removing an identity that is not there reports no change and mutates nothing, keyed on
        // the same declared identity an add uses.
        if (targeted.Count == 0)
            return false;

        // Removing the last entry drops the key rather than leaving it empty.
        tree.RemoveEntriesAt(key, [.. targeted.Select(e => e.Position)]);
        return true;
    }

    // ---- Classification ----

    /// <summary>
    /// Returns every key whose resulting value differs from its original. Values are compared after
    /// being re-emitted through the composer's settings rather than as raw text, so a
    /// reserialization that changed only quoting does not count as a change and cannot deny a write
    /// that changed nothing.
    /// </summary>
    /// <param name="original">The note's frontmatter as it stood.</param>
    /// <param name="result">The frontmatter the operations produced.</param>
    /// <returns>The changed key names, compared case-insensitively.</returns>
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
    /// Returns the op classes the write requires, as the union of three contributions: what the
    /// registry declares for every changed key, what a namespace prefix adds for those same keys,
    /// and what path policy's <c>mutation_requires</c> adds for the note itself. The last applies
    /// even on a body-only write, because a file's body can itself be an agent instruction.
    ///
    /// The union is computed over the outcome rather than the request. Classifying by intent would
    /// classify by what the caller said it was doing, and an operation aimed at an innocuous key
    /// can still mutate the protected layer.
    /// </summary>
    /// <param name="request">The write request, for its policy and registry.</param>
    /// <param name="changedKeys">The keys the write turned out to change.</param>
    /// <param name="mutates">Whether the write changes anything on disk.</param>
    /// <returns>The required op classes, empty where the write requires none.</returns>
    private static IReadOnlySet<string> Required(WriteRequest request, IReadOnlySet<string> changedKeys, bool mutates)
    {
        var required = KeyAuthority(request.Registry, changedKeys);

        // mutation_requires is the only modifier this path reads off the vault's policy. The read
        // floor beside it belongs to scope and never reaches here. A call that mutates nothing
        // mutates no target, so it contributes nothing.
        if (mutates)
            foreach (var opClass in request.Policy.MutationRequires(request.RelativePath))
                required.Add(opClass);

        return required;
    }

    /// <summary>Returns the op classes these keys require, from their declarations and from any
    /// namespace rule whose prefix they match.</summary>
    /// <param name="registry">The vault's loaded declaration.</param>
    /// <param name="keys">The keys to classify.</param>
    /// <returns>The required op classes, empty where the keys require none.</returns>
    private static HashSet<string> KeyAuthority(FrontmatterRegistry registry, IReadOnlyCollection<string> keys)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (registry.Keys.TryGetValue(key, out var declaration) && declaration.WriteRequires is { } declared)
                required.Add(declared);

            // A namespace match can only add to the requirement. Failing to match is not evidence
            // that a key needs no authority.
            foreach (var rule in registry.Namespaces)
                if (key.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                    required.Add(rule.WriteRequires);
        }

        return required;
    }

    // ---- Shared ----

    /// <summary>Parses the note's own block, refusing where it does not hold up.</summary>
    /// <param name="split">The note's split.</param>
    /// <returns>The parsed block, or an empty tree where the note has none.</returns>
    /// <exception cref="FrontmatterRefusal">The block will not parse or is not a mapping.</exception>
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

    /// <summary>
    /// Returns every key name the declaration recognizes, sorted ordinally so a refusal carrying
    /// them carries the same list on every call.
    /// </summary>
    /// <param name="registry">The vault's loaded declaration.</param>
    /// <returns>The declared key names, in ordinal order.</returns>
    /// <remarks>
    /// The declared-keys map alone. A namespace rule widens the authority a matching key requires
    /// and never adds a key, and the only caller of this is a refusal raised because a key is not
    /// in that map, so no prefix could have admitted it.
    /// </remarks>
    private static IReadOnlyList<string> DeclaredKeys(FrontmatterRegistry registry) =>
        [.. registry.Keys.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Returns the declaration's own spelling of a key, which canonicalization uses where
    /// the note does not already carry it.</summary>
    /// <param name="registry">The vault's loaded declaration.</param>
    /// <param name="key">The key as the caller spelled it.</param>
    /// <returns>The declared spelling, or null where the declaration does not name the key.</returns>
    private static string? DeclaredSpelling(FrontmatterRegistry registry, string key) =>
        registry.Keys.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns whether a text's first line is an opening fence, under the same rule the
    /// split applies: exactly <c>---</c> once trailing whitespace is discounted.</summary>
    /// <param name="text">The text whose first line is tested.</param>
    /// <returns>True when the first line is an opening fence.</returns>
    private static bool LooksLikeAnOpeningFence(string text)
    {
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).TrimEnd() is "---";
    }

    /// <summary>
    /// Returns the refusal a violation becomes when the text that produced it is the caller's own.
    /// The write path runs the validator's checks and refuses on their findings. The mapping lives
    /// here rather than on the rule, because whether a finding refuses depends on whose text it is.
    /// </summary>
    /// <param name="rule">The violation rule that fired.</param>
    /// <returns>The matching refusal reason.</returns>
    private static RefusalReason Reason(ViolationRule rule) => rule switch
    {
        ViolationRule.UnknownKey => RefusalReason.UnknownKey,
        ViolationRule.InlineValueWhereBlockListDeclared or ViolationRule.BlockWhereScalarDeclared =>
            RefusalReason.ShapeMismatch,
        _ => RefusalReason.ValueRefused,
    };
}

/// <summary>
/// Describes one frontmatter write. The registry, the policy and the authority arrive as arguments
/// rather than from a global or a container this layer reaches into, so a conformance suite can
/// hand in a synthetic vocabulary and the same code serves both front-ends.
/// </summary>
/// <param name="Content">The note as stored. Empty for a note being composed.</param>
/// <param name="RelativePath">The note's vault-relative path. Mandatory. Its only uses are the
/// exclusion refusal and matching path policy, and an absent path would read as permission on
/// exactly the files those protect.</param>
/// <param name="Registry">The vault's loaded declaration. A caller without one has no write to make
/// (<see cref="RegistryState.Require"/>).</param>
/// <param name="Policy">The vault's path policy. <see cref="PathPolicy.None"/> for a vault that
/// declares nothing.</param>
/// <param name="Authority">What asserts the required op classes once the outcome is known.</param>
/// <param name="Operations">The operations, applied in order, each seeing the previous result.
/// Repetition is meaningful.</param>
/// <param name="SuppliedBlock">A whole block the caller declares, with or without its fences, or
/// null. Validated whole, because the caller supplied every key.</param>
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
    /// <summary>Gets the operations to apply, in order, never null.</summary>
    public IReadOnlyList<FrontmatterOperation> Operations { get; } = Operations ?? [];
}

/// <summary>Describes what a write turned out to be.</summary>
/// <param name="Content">The composed note. Byte-identical to what the caller sent where nothing
/// changed.</param>
/// <param name="Split">The frontmatter and body of <paramref name="Content"/>, from the one
/// definition of the split, so what a tool echoes back is byte-identical to what a reader
/// returns.</param>
/// <param name="Violations">Schema violations under the keys the operations touched, reported as
/// data. A single-note tool treats a non-empty list as failure and a sweeping tool renders
/// findings; this path decides for neither.</param>
/// <param name="AppliedOperations">The indices, into the request's own operation list, of the
/// operations that changed something. 0-based, because it indexes a list the caller holds.</param>
/// <param name="FrontmatterChanged">Whether the frontmatter's bytes differ from the note's. A
/// reserialization that changed only quoting is a byte change and not a
/// <paramref name="ChangedKeys"/> one.</param>
/// <param name="BodyChanged">Whether the body's bytes differ from the note's.</param>
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
/// Asserts the authority a write turned out to require. The write path asserts here rather than
/// returning the requirement for a caller to assert, because returning it would make the assertion
/// optional at every call site. The per-call access object implements this, which is how the
/// assertion reaches this path without a vault type entering the namespace.
/// </summary>
public interface IKeyAuthority
{
    /// <summary>
    /// Throws where <paramref name="required"/> is not held. An empty set asserts nothing, and the
    /// write path does not call this with one.
    /// </summary>
    /// <param name="required">The op classes the outcome requires, as a union.</param>
    /// <param name="changedKeys">The call's own coordinates, for a denial to name. Naming them
    /// discloses nothing about what the caller holds.</param>
    void Assert(IReadOnlySet<string> required, IReadOnlySet<string> changedKeys);
}
