using VaultShared.Auth;
using VaultShared.Frontmatter;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// Pairs one resolved vault with the caller's grant. It is the main producer of admitted paths, and
/// holding the caller's authority is all it does.
/// </summary>
/// <remarks>
/// No member here takes a scope argument, and none may be added: a caller-supplied scope would be a
/// forged grant, and the strongest defence is that no such argument exists. The constructor takes
/// the whole grant rather than a bare scope because this layer needs two things from it - path
/// admission from the grant's scope in this vault, and the mid-operation key assertion from the
/// same grant's op classes.
///
/// Both enumeration surfaces and all three search granularities are members here rather than free
/// functions, so their results are filtered against the effective read scope without each caller
/// having to remember to. Filtering is silent: a list returns the in-scope subset and does not say
/// what was withheld.
/// </remarks>
public sealed class VaultAccess : IKeyAuthority
{
    private readonly Grant _grant;
    private readonly VaultScope _grantScope;
    private readonly VaultScope _readScope;
    private readonly SearchEngine _engine;

    /// <summary>Initializes access to one vault under one grant.</summary>
    /// <param name="vault">The vault this access object is scoped to.</param>
    /// <param name="grant">
    /// The caller's grant. It must carry a scope entry for <paramref name="vault"/>. A grant that
    /// does not is a defect upstream and throws, rather than being read as permission.
    /// </param>
    /// <param name="engine">
    /// Where the search engine is, passed in like the grant and never read from ambient state.
    /// Omitted means the engine as <c>PATH</c> resolves it, which is what a host configuring no
    /// explicit location falls back to.
    /// </param>
    public VaultAccess(Vault vault, Grant grant, SearchEngine? engine = null)
    {
        Vault = vault;
        _grant = grant;
        _grantScope = grant.ScopeIn(vault.Name);
        _engine = engine ?? SearchEngine.OnPath;

        // Computed once here rather than per candidate, so a whole-vault enumeration filters
        // against one scope instead of rebuilding the union for every path it considers.
        _readScope = vault.Policy.EffectiveReadScope(_grantScope);
    }

    /// <summary>Gets the vault this access object is scoped to.</summary>
    public Vault Vault { get; }

    /// <summary>
    /// Resolves a path the caller named for reading, against the effective read scope: the grant's
    /// own roots unioned with this vault's always-readable roots.
    /// </summary>
    /// <param name="relativePath">The vault-relative path as the caller wrote it.</param>
    /// <returns>The admitted read proof, or a refusal.</returns>
    public PathResolution<ReadPath> ForRead(string relativePath) =>
        Resolve(relativePath, _readScope, path => new ReadPath(path));

    /// <summary>
    /// Resolves a path the caller named for writing, against the grant's own roots and nothing
    /// wider. An always-readable root admits a read here and refuses a write, which is the
    /// asymmetry the two path types carry.
    /// </summary>
    /// <param name="relativePath">The vault-relative path as the caller wrote it.</param>
    /// <returns>The admitted write proof, or a refusal.</returns>
    public PathResolution<WritePath> ForWrite(string relativePath) =>
        Resolve(relativePath, _grantScope, path => new WritePath(path));

    /// <summary>
    /// Returns whether a path this layer produced itself falls in the effective read scope. A
    /// caller filtering a list with this returns the in-scope subset and does not say what was
    /// withheld.
    /// </summary>
    /// <remarks>
    /// This is a separate member from <see cref="ForRead"/> rather than the same one with a flag,
    /// because it cannot construct a refusal and so cannot leak one.
    ///
    /// The dot rule is not applied here. It is a property of a path the caller named, and an
    /// enumeration that admits dot entries still filters them by scope.
    /// </remarks>
    /// <param name="candidate">A path this layer produced, in this vault.</param>
    /// <returns>True when the effective read scope admits the path.</returns>
    /// <exception cref="ArgumentException"><paramref name="candidate"/> belongs to another vault.</exception>
    public bool Admits(VaultPath candidate)
    {
        if (candidate.Vault != Vault)
            throw new ArgumentException(
                $"Candidate path belongs to vault \"{candidate.Vault.Name}\", not \"{Vault.Name}\".", nameof(candidate));

        return _readScope.Admits(candidate.Value);
    }

    /// <summary>
    /// Returns every markdown file in the vault, or those matching a glob. This backs every
    /// operation whose subject is a note.
    /// </summary>
    /// <param name="pattern">
    /// Standard glob syntax with <c>**</c> for recursive descent, matched case-insensitively. A
    /// pattern containing no separator matches a path's final segment at any depth; one containing a
    /// separator is anchored at the vault root. Null or empty returns everything.
    /// </param>
    /// <param name="includeDotEntries">
    /// Whether to return paths with a dot-prefixed segment at any depth. Off by default, which is
    /// what keeps a caller off the vault copies living under a dot-directory.
    /// </param>
    /// <returns>The matching notes, in scope, or a search failure.</returns>
    public SearchOutcome<VaultPath> EnumerateNotes(string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Enumerate(Vault, _engine, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// Returns every file in the vault whatever its extension, or those matching a glob. This backs
    /// whole-vault passes that must not miss a non-markdown file, where omitting one silently is a
    /// wrong answer rather than a narrower one.
    /// </summary>
    /// <inheritdoc cref="EnumerateNotes" path="/param"/>
    /// <returns>The matching files, in scope, or a search failure.</returns>
    public SearchOutcome<VaultPath> EnumerateFiles(string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Enumerate(Vault, _engine, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: false));

    /// <summary>
    /// Searches note content by regex and returns each matching line with its location. This is the
    /// richest of the three search granularities.
    /// </summary>
    /// <param name="regex">The engine's regex syntax. A malformed regex is a caller error.</param>
    /// <param name="pattern">An optional glob narrowing which notes are searched, as in <see cref="EnumerateNotes"/>.</param>
    /// <param name="includeDotEntries">As in <see cref="EnumerateNotes"/>, and off by default here too.</param>
    /// <returns>The matching lines, in scope, or a search failure.</returns>
    public SearchOutcome<MatchLine> SearchLines(string regex, string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Lines(Vault, _engine, regex, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// Searches note content by regex and returns the notes that contain a match. It is a separate
    /// operation rather than <see cref="SearchLines"/> with the lines discarded, so a caller wanting
    /// only a file list does not pay to materialize every match.
    /// </summary>
    /// <inheritdoc cref="SearchLines" path="/param"/>
    /// <returns>The matching notes, in scope, or a search failure.</returns>
    public SearchOutcome<VaultPath> SearchFiles(string regex, string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Containing(Vault, _engine, regex, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// Searches note content by regex and returns how many lines matched in each note that has a
    /// match. It is a separate operation for the same reason <see cref="SearchFiles"/> is.
    /// </summary>
    /// <inheritdoc cref="SearchLines" path="/param"/>
    /// <returns>The per-note counts, in scope, or a search failure.</returns>
    public SearchOutcome<MatchCount> SearchCounts(string regex, string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Counts(Vault, _engine, regex, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// Builds the predicate that applies scope, the dot rule, and note enumeration's markdown
    /// restriction to what the engine returned.
    /// </summary>
    /// <remarks>
    /// Do not move scope or the markdown restriction into a glob passed to the engine, either as a
    /// second <c>--iglob</c> or appended to the caller's own. Positive globs union rather than
    /// intersect, so a second one widens the result instead of narrowing it. Appending is worse:
    /// <c>notes/**</c> with <c>.md</c> appended is <c>notes/**.md</c>, which the engine accepts and
    /// treats as a single <c>*</c>, so every nested note silently disappears from the result. A
    /// positive glob also overrides <c>--type</c>, so the markdown restriction cannot use that flag
    /// either whenever the caller supplied a pattern, which is why it never does.
    ///
    /// The dot rule is the exception. A negative glob does intersect, so with dot entries off it
    /// also prunes the engine's walk. This predicate applies the rule as well, so nothing depends
    /// on the engine's glob semantics alone.
    ///
    /// The markdown test is case-insensitive, because names already on disk are never checked
    /// against the name grammar, so a note spelled <c>.MD</c> stays enumerable, searchable and
    /// movable.
    /// </remarks>
    /// <param name="includeDotEntries">Whether to admit paths with a dot-prefixed segment.</param>
    /// <param name="notesOnly">Whether to admit only markdown files.</param>
    /// <returns>A predicate that admits a path the caller may see.</returns>
    private Func<VaultPath, bool> Filter(bool includeDotEntries, bool notesOnly) =>
        path =>
            (includeDotEntries || !path.HasDotSegment)
            && (!notesOnly || path.Value.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            && Admits(path);

    /// <summary>
    /// Admits a path this layer produced itself, given a write proof for a folder that contains it.
    /// </summary>
    /// <remarks>
    /// This widens nothing. Scope roots are whole-segment prefixes, so everything under an admitted
    /// folder is admitted by the same root. The containment relation is checked here rather than
    /// assumed, and a path that is not under the folder is a defect at the call site rather than a
    /// scope answer.
    ///
    /// It exists for the one operation whose targets are derived rather than named: a recursive
    /// removal works from a plan the file census produced, and those entries include the
    /// dot-prefixed paths a caller may not name.
    ///
    /// Keep this a member of this type, for the same reason <see cref="BypassingScope"/> is one.
    /// Producing an admitted path anywhere else is how the scope check gets skipped without saying
    /// so, and every site doing something unusual with admission should be findable with one
    /// search.
    /// </remarks>
    /// <param name="folder">A write proof for the folder, or for the path itself.</param>
    /// <param name="contained">The path to admit, which must be the folder or beneath it.</param>
    /// <returns>A write proof for the contained path.</returns>
    /// <exception cref="ArgumentException"><paramref name="contained"/> is not the folder or beneath
    /// it, or belongs to another vault.</exception>
    public WritePath Beneath(WritePath folder, VaultPath contained)
    {
        if (contained.Vault != Vault)
            throw new ArgumentException(
                $"Path belongs to vault \"{contained.Vault.Name}\", not \"{Vault.Name}\".", nameof(contained));

        if (!contained.Equals(folder.Path)
            && !contained.Value.StartsWith(folder.Path.Value + '/', StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"\"{contained.Value}\" is not \"{folder.Path.Value}\" and is not beneath it.", nameof(contained));

        return new WritePath(contained);
    }

    /// <summary>
    /// Returns an object offering unscoped enumerate, read and write over the whole vault. It is
    /// named for what it does rather than for what it is used for, so that bypassing scope reads as
    /// a deliberate act at the call site and every site doing it can be found with one search.
    /// </summary>
    /// <remarks>
    /// Wikilink maintenance is the only work that needs it; see <see cref="VaultWidePass"/>. The
    /// operation's own paths are still resolved and scope-checked through the members above.
    /// </remarks>
    /// <returns>The vault-wide pass.</returns>
    public VaultWidePass BypassingScope() => new(Vault, _engine);

    /// <summary>
    /// Asserts that the grant holds a capability key, mid-operation, once the outcome is known.
    /// What the caller requested is checked above this layer; this is for authority that depends on
    /// what the work turned out to touch.
    /// </summary>
    /// <param name="opClass">The op class the operation requires.</param>
    /// <exception cref="AuthorityRefusal">The grant does not hold <paramref name="opClass"/>.</exception>
    public void AssertKey(string opClass)
    {
        if (!_grant.Allows(opClass))
            throw new AuthorityRefusal(opClass);
    }

    /// <summary>
    /// Asserts the op classes a frontmatter change requires, over the same grant and the same check
    /// <see cref="AssertKey"/> makes. The frontmatter surface holds this object as an
    /// <see cref="IKeyAuthority"/> rather than as a vault, which is how the key assertion reaches a
    /// namespace that knows nothing about vaults.
    /// </summary>
    /// <remarks>
    /// The exception names the changed key set rather than the op class that failed. The key set is
    /// the caller's own input; which class the grant lacks is information about the grant and must
    /// not be disclosed.
    /// </remarks>
    /// <param name="required">The op classes the change requires.</param>
    /// <param name="changedKeys">The frontmatter keys the change touches, named in the refusal.</param>
    /// <exception cref="FrontmatterRefusal">The grant does not hold every op class in
    /// <paramref name="required"/>.</exception>
    void IKeyAuthority.Assert(IReadOnlySet<string> required, IReadOnlySet<string> changedKeys)
    {
        try
        {
            foreach (var opClass in required)
                AssertKey(opClass);
        }
        catch (AuthorityRefusal)
        {
            throw FrontmatterRefusal.Denied(changedKeys);
        }
    }

    /// <summary>
    /// Resolves a path the caller named, for both <see cref="ForRead"/> and <see cref="ForWrite"/>.
    /// </summary>
    /// <remarks>
    /// Do not reorder the checks. Text that is not a path at all is refused first, then the
    /// dot-segment rule that no grant changes, then scope. Every refusal names the path as the
    /// caller wrote it and says nothing further.
    /// </remarks>
    /// <typeparam name="TPath">The admitted proof: <see cref="ReadPath"/> or <see cref="WritePath"/>.</typeparam>
    /// <param name="relativePath">The vault-relative path as the caller wrote it.</param>
    /// <param name="scope">The scope to check against.</param>
    /// <param name="admit">Wraps a contained path as the admitted proof.</param>
    /// <returns>The admitted proof, or a refusal.</returns>
    private PathResolution<TPath> Resolve<TPath>(string relativePath, VaultScope scope, Func<VaultPath, TPath> admit)
        where TPath : class
    {
        if (!VaultPath.TryCreate(Vault, relativePath, out var path, out var reason))
            return new PathResolution<TPath>.Malformed(relativePath, reason);

        if (path.HasDotSegment)
            return new PathResolution<TPath>.Excluded(relativePath);

        if (!scope.Admits(path.Value))
            return new PathResolution<TPath>.OutOfScope(relativePath);

        return new PathResolution<TPath>.Admitted(admit(path));
    }
}
