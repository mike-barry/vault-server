using VaultShared.Health;

namespace VaultShared.Access;

/// <summary>
/// Interprets a host's configured search-engine location into a <see cref="SearchEngine"/>. Every
/// front-end reads the location from its own configuration and then comes through here, so the
/// fallback, the base a relative path resolves against, and the existence check are decided once and
/// cannot drift between front-ends.
/// </summary>
/// <remarks>
/// This does not weaken the rule that the access layer never discovers the engine's location for
/// itself. The caller supplies the configured text and the directory to resolve it against; nothing
/// here reads an environment variable, a configuration file, or any other ambient source. What is
/// per-host is where the value comes from, and that is the argument.
/// </remarks>
public static class SearchEngines
{
    /// <summary>The subsystem name recorded with a problem, so an operator sees which load found it.</summary>
    private const string Source = "SearchEngine";

    /// <summary>
    /// Returns the engine the configured location names, or the engine as <c>PATH</c> resolves it
    /// when nothing is configured.
    /// </summary>
    /// <param name="configured">
    /// The engine's location exactly as the host's configuration holds it, which may be relative.
    /// Null or empty means the engine is found on <c>PATH</c>, which is the shipped default.
    /// </param>
    /// <param name="configDirectory">
    /// The directory a relative <paramref name="configured"/> is resolved against, which is the
    /// directory the host's own configuration lives in. Resolving against the process's working
    /// directory instead would give a different engine per invocation of a command-line front-end,
    /// which is run from wherever an operator happens to be.
    /// </param>
    /// <param name="sink">
    /// Where a configured location that is not there is recorded, as
    /// <see cref="ProblemSeverity.Degraded"/>. Fatal would take down a host whose reads, writes,
    /// moves and removals are all still correct, since only search and enumeration go through the
    /// engine, and recording it here names the mistyped path at startup rather than leaving an
    /// operator to infer it from the first search that refuses.
    /// </param>
    /// <returns>
    /// The engine to hand to the access layer. A configured location that is not there is still
    /// returned rather than replaced by the <c>PATH</c> fallback, so every search refuses instead of
    /// silently running a different executable than the one configured.
    /// </returns>
    public static SearchEngine Load(string? configured, string configDirectory, IConfigProblemSink sink)
    {
        if (configured is not { Length: > 0 } declared)
            return SearchEngine.OnPath;

        // Only an explicitly configured location is checked. Absence means the engine is found on
        // PATH, and probing PATH for it is the platform's job at launch, not this method's.
        var path = Path.GetFullPath(Path.Combine(configDirectory, declared));
        if (!File.Exists(path))
        {
            sink.RecordDegraded(
                Source,
                $"The configured search engine executable is not there ({path}) - every search and enumeration refuses until it is, and the operations that do not search are unaffected.");
        }

        return new SearchEngine(path);
    }
}
