using VaultShared.Auth;
using VaultShared.Frontmatter;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// One resolved vault plus the caller's grant, and the only producer of an admitted path. Holding
/// the caller's authority is the whole of what it is.
///
/// Authority is handed in rather than ambient because the constructor takes an object the caller
/// must already hold, and no member here takes a scope - a caller-supplied scope is a forged grant,
/// and the strongest defence is that no such argument exists. It takes the grant rather than a bare
/// scope because the layer owes authority two things: path admission from the grant's scope in this
/// vault, and the mid-operation key assertion from the same grant's op classes.
///
/// Both enumeration surfaces and all three search granularities are operations here rather than
/// free functions, so their results are filtered against the effective read scope by construction
/// rather than by each caller remembering to. Filtering is silent: a list returns the in-scope
/// subset without saying what was withheld.
/// </summary>
public sealed class VaultAccess : IKeyAuthority
{
    private readonly Grant _grant;
    private readonly VaultScope _grantScope;
    private readonly VaultScope _readScope;
    private readonly SearchEngine _engine;

    /// <param name="grant">
    /// The caller's grant. It must carry a scope entry for <paramref name="vault"/>; a grant that
    /// does not is an upstream defect and throws rather than being interpreted as permission.
    /// </param>
    /// <param name="engine">
    /// Where the search engine is, handed in like the grant and never read from ambient state.
    /// Omitted means the engine as <c>PATH</c> resolves it, which is what a host configuring no
    /// explicit location falls back to.
    /// </param>
    public VaultAccess(Vault vault, Grant grant, SearchEngine? engine = null)
    {
        Vault = vault;
        _grant = grant;
        _grantScope = grant.ScopeIn(vault.Name);
        _engine = engine ?? SearchEngine.OnPath;

        // The effective read scope is computed once, here, rather than per candidate: a whole-vault
        // enumeration filters against one scope instead of rebuilding the union for every path it
        // considers.
        _readScope = vault.Policy.EffectiveReadScope(_grantScope);
    }

    public Vault Vault { get; }

    /// <summary>
    /// Resolves a caller-named path for reading, against the effective read scope - the grant's own
    /// roots unioned with this vault's always-readable roots.
    /// </summary>
    public PathResolution<ReadPath> ForRead(string relativePath) =>
        Resolve(relativePath, _readScope, path => new ReadPath(path));

    /// <summary>
    /// Resolves a caller-named path for writing, against the grant's own roots and nothing wider.
    /// An always-readable root admits a read here and refuses a write, which is the asymmetry the
    /// two path types carry.
    /// </summary>
    public PathResolution<WritePath> ForWrite(string relativePath) =>
        Resolve(relativePath, _grantScope, path => new WritePath(path));

    /// <summary>
    /// The quiet path: whether a path the layer itself produced falls in the effective read scope.
    /// A list returns the in-scope subset without saying what was withheld.
    ///
    /// Separate from <see cref="ForRead"/> rather than the same member with a flag, because this one
    /// cannot construct a refusal and so cannot leak one. The dot rule is not applied here - the
    /// perimeter is a property of a caller-named path, and an enumeration that admits dot entries
    /// still filters them by scope.
    ///
    /// A candidate from another vault is a layer defect rather than a scope answer, and throws.
    /// </summary>
    public bool Admits(VaultPath candidate)
    {
        if (candidate.Vault != Vault)
            throw new ArgumentException(
                $"Candidate path belongs to vault \"{candidate.Vault.Name}\", not \"{Vault.Name}\".", nameof(candidate));

        return _readScope.Admits(candidate.Value);
    }

    /// <summary>
    /// Every markdown file in the vault, or those matching <paramref name="pattern"/>. What backs
    /// everything whose subject is a note.
    /// </summary>
    /// <param name="pattern">
    /// Standard glob syntax with <c>**</c> for recursive descent, matched case-insensitively. A
    /// pattern containing no separator matches a path's final segment at any depth; one containing a
    /// separator is anchored at the vault root. Null or empty returns everything in the domain.
    /// </param>
    /// <param name="includeDotEntries">
    /// Whether a path with a dot-prefixed segment at any depth is returned. Off by default, which is
    /// what keeps a caller off the vault copies living under a dot-directory.
    /// </param>
    public SearchOutcome<VaultPath> EnumerateNotes(string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Enumerate(Vault, _engine, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// Every file in the vault whatever its extension, or those matching <paramref name="pattern"/>.
    /// The census: it backs whole-vault passes that must not miss a non-markdown file, where a
    /// silent omission is a wrong answer rather than a narrower one.
    /// </summary>
    /// <inheritdoc cref="EnumerateNotes" path="/param"/>
    public SearchOutcome<VaultPath> EnumerateFiles(string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Enumerate(Vault, _engine, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: false));

    /// <summary>
    /// Regex search over note content, answering each matching line with its location. The richest
    /// of the three granularities.
    /// </summary>
    /// <param name="regex">The engine's regex syntax. A malformed one is a caller error.</param>
    /// <param name="pattern">An optional glob narrowing which notes are searched, as in <see cref="EnumerateNotes"/>.</param>
    /// <param name="includeDotEntries">As in <see cref="EnumerateNotes"/>, and off by default here too.</param>
    public SearchOutcome<MatchLine> SearchLines(string regex, string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Lines(Vault, _engine, regex, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// The set of notes containing a match. Its own capability rather than <see cref="SearchLines"/>
    /// with the lines discarded, because a caller wanting a file list should not pay to materialize
    /// every match in order to throw them away.
    /// </summary>
    /// <inheritdoc cref="SearchLines" path="/param"/>
    public SearchOutcome<VaultPath> SearchFiles(string regex, string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Containing(Vault, _engine, regex, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// How many lines matched in each note that has a match, for the same reason
    /// <see cref="SearchFiles"/> is its own capability.
    /// </summary>
    /// <inheritdoc cref="SearchLines" path="/param"/>
    public SearchOutcome<MatchCount> SearchCounts(string regex, string? pattern = null, bool includeDotEntries = false) =>
        VaultSearch.Counts(Vault, _engine, regex, pattern, !includeDotEntries, Filter(includeDotEntries, notesOnly: true));

    /// <summary>
    /// Scope, the dot rule, and note enumeration's markdown restriction, as one predicate over what
    /// the engine returned.
    ///
    /// Neither scope nor the markdown restriction may be expressed as a second <c>--iglob</c> or
    /// appended to the caller's own, and the reason is that positive globs union rather than
    /// intersect. A second positive glob widens the answer instead of narrowing it, and appending
    /// is worse than useless: <c>notes/**</c> with <c>.md</c> appended is <c>notes/**.md</c>, which
    /// the engine accepts and degrades to a single <c>*</c>, so every nested note silently
    /// disappears from the result. A positive glob also overrides <c>--type</c>, so the markdown
    /// restriction cannot ride that flag either whenever a caller supplied a pattern - which is why
    /// it never does. The dot rule is the exception: a negative glob intersects, so with dot
    /// entries off it also prunes the engine's walk, and this predicate keeps the rule beside that
    /// pruning rather than depending on it.
    ///
    /// The markdown test is case-insensitive: names already on disk are not checked against the name
    /// grammar, so a note spelled <c>.MD</c> stays enumerable, searchable and movable.
    /// </summary>
    private Func<VaultPath, bool> Filter(bool includeDotEntries, bool notesOnly) =>
        path =>
            (includeDotEntries || !path.HasDotSegment)
            && (!notesOnly || path.Value.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            && Admits(path);

    /// <summary>
    /// The vault-wide pass: unscoped enumerate, read and write, on an object of their own. Named
    /// for what it does rather than for what it is used for, because the point of the seam is that
    /// bypassing scope reads as a deliberate act at the call site and that every site doing it is
    /// one grep away.
    ///
    /// Wikilink maintenance is the one pass scope does not bound - see <see cref="VaultWidePass"/>
    /// for why. The operation's own paths are still resolved and scope-checked through the members
    /// above.
    /// </summary>
    public VaultWidePass BypassingScope() => new(Vault, _engine);

    /// <summary>
    /// Asserts a capability key mid-operation, once the outcome is known. The floor for what was
    /// <em>requested</em> is checked above the layer; this is for authority that depends on what the
    /// work turned out to touch.
    /// </summary>
    /// <exception cref="AuthorityRefusal">The grant does not hold <paramref name="opClass"/>.</exception>
    public void AssertKey(string opClass)
    {
        if (!_grant.Allows(opClass))
            throw new AuthorityRefusal(opClass);
    }

    /// <summary>
    /// The frontmatter surface's authority seam, over the same grant and the same assertion
    /// <see cref="AssertKey"/> makes. The surface holds this object as an
    /// <see cref="IKeyAuthority"/> rather than as a vault, which is how the mid-operation key
    /// assertion reaches a namespace that knows nothing about vaults.
    ///
    /// The denial it raises names the changed key set rather than the op class that failed. That set
    /// is the call's own coordinates; which class the grant lacks is information about the grant.
    /// </summary>
    /// <exception cref="FrontmatterRefusal">The grant holds none of <paramref name="required"/>.</exception>
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
    /// The loud path, shared by both entry points. The order is fixed: text that is not a path at
    /// all, then the perimeter no grant changes, then scope. Every refusal names the path as the
    /// caller wrote it and says nothing further.
    /// </summary>
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
