using VaultShared.Health;

namespace VaultShared.Access;

/// <summary>
/// Interprets a host's configured search-engine location into a <see cref="SearchEngine"/>. Every
/// front-end reads the location from its own configuration and then comes through here, so the
/// fallback, the base a relative path resolves against, and the existence check are decided once and
/// cannot drift between front-ends.
/// </summary>
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
    /// directory would give a different engine per invocation of a command-line front-end.
    /// </param>
    /// <param name="sink">
    /// Where a configured location that is not there is recorded, as
    /// <see cref="ProblemSeverity.Degraded"/> rather than fatal, since only search and enumeration
    /// go through the engine.
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
        // PATH, which the platform does at launch.
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
