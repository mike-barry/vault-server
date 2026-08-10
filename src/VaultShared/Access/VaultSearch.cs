using System.Text.Json;

namespace VaultShared.Access;

/// <summary>
/// Drives the search engine: the arguments each surface asks for, the classification of what came
/// back, and the parse of the shapes the engine emits. Scope, the dot rule and the markdown
/// restriction arrive as a predicate <see cref="VaultAccess"/> composes and hands in, because they
/// are properties of the caller's authority rather than of the engine.
/// </summary>
/// <remarks>
/// Every surface here runs the engine once and reads both of its streams whole before returning.
/// </remarks>
internal static class VaultSearch
{
    /// <summary>
    /// The arguments every invocation carries. Each is required. <c>--no-config</c>, or the engine
    /// takes options from a file named by an environment variable, which is ambient state nothing
    /// here may read. <c>--hidden</c>, because the engine's hidden-file skipping is not the dot
    /// rule. Hidden on Windows is a file attribute, which a dot-prefixed directory does not carry,
    /// so leaving it off would drop dot entries the file census must see while still admitting
    /// hidden ones. <c>--no-ignore</c>, because a gitignored file is still on disk and can still be
    /// destroyed, so no ignore-file behaviour is inherited.
    /// </summary>
    private static readonly string[] Always = ["--no-config", "--hidden", "--no-ignore"];

    /// <summary>
    /// The operand every invocation ends with, naming the directory the engine was started in. That
    /// is the vault root, which is what makes every emitted path vault-relative.
    ///
    /// It must be stated explicitly. With nothing to search named, the engine searches standard
    /// input whenever standard input is redirected, and a process started as a child always has it
    /// redirected. The result would be a search of nothing, reported as a successful empty result.
    /// </summary>
    private static readonly string[] Here = ["--", "."];

    /// <summary>
    /// Returns every file the engine walks, filtered by the caller's predicate. Both enumeration
    /// surfaces come through here, and the predicate is what separates them.
    /// </summary>
    /// <remarks>
    /// No completion marker is asked for here. Ripgrep ignores <c>--stats</c> when <c>--files</c> is
    /// given, measured against ripgrep 15.2.0, so a walk that fails before emitting a path is
    /// classified as a malformed pattern rather than an operational failure.
    /// </remarks>
    /// <param name="vault">The vault to search.</param>
    /// <param name="engine">The engine to run.</param>
    /// <param name="pattern">An optional glob narrowing which files are walked.</param>
    /// <param name="pruneDotEntries">Whether to exclude dot-prefixed entries from the walk.</param>
    /// <param name="admit">Decides which emitted paths the caller may see.</param>
    /// <returns>The admitted paths, or a search failure.</returns>
    internal static SearchOutcome<VaultPath> Enumerate(
        Vault vault, SearchEngine engine, string? pattern, bool pruneDotEntries, Func<VaultPath, bool> admit)
    {
        var run = engine.Run(vault.Root, [.. Always, .. Glob(pattern, pruneDotEntries), "--files", "--null", .. Here]);
        return Read(run, vault, output => Paths(vault, Split(output, trailing: false), admit));
    }

    /// <summary>Returns each matching line with its location.</summary>
    /// <param name="vault">The vault to search.</param>
    /// <param name="engine">The engine to run.</param>
    /// <param name="regex">The engine's regex syntax.</param>
    /// <param name="pattern">An optional glob narrowing which files are searched.</param>
    /// <param name="pruneDotEntries">Whether to exclude dot-prefixed entries from the walk.</param>
    /// <param name="admit">Decides which emitted paths the caller may see.</param>
    /// <returns>The matching lines, or a search failure.</returns>
    internal static SearchOutcome<MatchLine> Lines(
        Vault vault, SearchEngine engine, string regex, string? pattern, bool pruneDotEntries, Func<VaultPath, bool> admit)
    {
        // --json is the only output shape carrying the path, the line number and the line's text as
        // separate fields, so none of them has to be recovered from a rendering that a path
        // containing a colon would make ambiguous. Its summary event is also the completion marker
        // Read below relies on.
        var run = engine.Run(vault.Root, [.. Always, .. Glob(pattern, pruneDotEntries), "--json", "--regexp", regex, .. Here]);
        return Read(run, vault, output => Matches(vault, output, admit));
    }

    /// <summary>
    /// Returns the files containing a match, as its own invocation rather than the line granularity
    /// with the lines dropped, so a caller wanting a file list does not pay to materialize every
    /// match.
    /// </summary>
    /// <inheritdoc cref="Lines" path="/param"/>
    /// <returns>The matching files, or a search failure.</returns>
    internal static SearchOutcome<VaultPath> Containing(
        Vault vault, SearchEngine engine, string regex, string? pattern, bool pruneDotEntries, Func<VaultPath, bool> admit)
    {
        var run = engine.Run(
            vault.Root, [.. Always, .. Glob(pattern, pruneDotEntries), "--files-with-matches", "--null", "--stats", "--regexp", regex, .. Here]);
        return Read(run, vault, output => Paths(vault, Split(output, trailing: true), admit));
    }

    /// <summary>
    /// Returns how many lines matched in each file, again as its own invocation.
    /// </summary>
    /// <inheritdoc cref="Lines" path="/param"/>
    /// <returns>The per-file counts, or a search failure.</returns>
    internal static SearchOutcome<MatchCount> Counts(
        Vault vault, SearchEngine engine, string regex, string? pattern, bool pruneDotEntries, Func<VaultPath, bool> admit)
    {
        var run = engine.Run(
            vault.Root, [.. Always, .. Glob(pattern, pruneDotEntries), "--count", "--null", "--stats", "--regexp", regex, .. Here]);
        return Read(run, vault, output => Counted(vault, output, admit));
    }

    /// <summary>
    /// Builds the glob arguments. The caller's pattern reaches the engine as <c>--iglob</c>, which
    /// supplies both <c>**</c> and case-insensitivity, and whose anchoring is the one required: a
    /// pattern with no separator matches a path's final segment at any depth, and one with a
    /// separator is anchored at the vault root.
    /// </summary>
    /// <remarks>
    /// With dot entries off, the dot rule also prunes the walk. A negative glob intersects where a
    /// positive one unions, and a directory it excludes is never entered, so a default search does
    /// not read the object store under <c>.git</c> and cannot fail on a file in there that a
    /// concurrent git operation holds locked.
    ///
    /// Keep the dot glob after the caller's own. The engine gives the last matching glob
    /// precedence, and no caller pattern may reach past the dot rule while it is on. The caller's
    /// predicate applies the rule as well, so nothing depends on the engine's glob semantics alone.
    /// </remarks>
    /// <param name="pattern">The caller's glob, or null or empty for none.</param>
    /// <param name="pruneDotEntries">Whether to exclude dot-prefixed entries from the walk.</param>
    /// <returns>The glob arguments, in order.</returns>
    private static string[] Glob(string? pattern, bool pruneDotEntries)
    {
        string[] caller = string.IsNullOrEmpty(pattern) ? [] : ["--iglob", pattern];
        return pruneDotEntries ? [.. caller, "--iglob", "!.*"] : caller;
    }

    /// <summary>
    /// Classifies a completed run into one of the five outcomes, from the exit code and the streams
    /// rather than from message text wherever possible.
    /// </summary>
    /// <remarks>
    /// The engine refuses a malformed glob or regex before it walks anything, so such a run
    /// produces no output at all. Every other exit means it ran: it either completed (0 or 1) or
    /// completed and also reported a problem (2, with the output it had already produced). That is
    /// the only discriminator between a caller's bad pattern and a failure during the search.
    ///
    /// The search granularities therefore ask the engine for a completion marker they do not
    /// otherwise need. The JSON summary event and the <c>--stats</c> block are printed even when
    /// the run also failed, so "produced no output" cannot be confused with "matched nothing". Do
    /// not drop those flags.
    ///
    /// Anything on the error stream fails the whole call, rather than answering with the files the
    /// run could read.
    /// </remarks>
    /// <typeparam name="TResult">What one result is.</typeparam>
    /// <param name="run">The completed run.</param>
    /// <param name="vault">The vault that was searched.</param>
    /// <param name="parse">Parses the engine's output into results.</param>
    /// <returns>The parsed results, or a classified failure.</returns>
    private static SearchOutcome<TResult> Read<TResult>(
        EngineRun run, Vault vault, Func<string, SearchOutcome<TResult>> parse)
    {
        if (run.ExitCode == SearchEngine.LaunchFailed)
            return new SearchOutcome<TResult>.EngineUnavailable(run.StandardError);

        var ran = run.ExitCode is 0 or 1 || run.StandardOutput.Length > 0;

        if (run.StandardError.Length > 0)
        {
            var text = run.StandardError.Trim();
            if (!ran)
                return new SearchOutcome<TResult>.BadPattern(text);

            return Entry(vault, text) is { } entry
                ? new SearchOutcome<TResult>.Unreadable(entry, text)
                : new SearchOutcome<TResult>.Failed(text);
        }

        return run.ExitCode is 0 or 1
            ? parse(run.StandardOutput)
            : new SearchOutcome<TResult>.Failed($"The search engine exited with code {run.ExitCode}.");
    }

    /// <summary>
    /// Returns the entry a diagnostic is about, when it is about one. The engine attributes a
    /// failure it hit during the walk to the entry it hit it on, as
    /// <c>rg: &lt;path&gt;: &lt;reason&gt;</c>, and writes nothing that parses as a path when the
    /// failure is its own. The paths are already vault-relative, since the engine was started in
    /// the vault directory.
    /// </summary>
    /// <param name="vault">The vault that was searched.</param>
    /// <param name="diagnostics">Everything the run wrote to standard error.</param>
    /// <returns>The entry, vault-relative, or null when the diagnostic names none.</returns>
    private static string? Entry(Vault vault, string diagnostics)
    {
        foreach (var line in diagnostics.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith("rg: ", StringComparison.Ordinal))
                continue;

            var body = text["rg: ".Length..];
            var end = body.IndexOf(": ", StringComparison.Ordinal);
            if (end > 0 && VaultPath.TryCreate(vault, body[..end], out var path, out _))
                return path.Value;
        }

        return null;
    }

    /// <summary>
    /// Splits NUL-separated output into paths, so a path containing a colon, a space or a newline
    /// still parses.
    /// </summary>
    /// <param name="output">Everything the run wrote to standard output.</param>
    /// <param name="trailing">
    /// True when the engine appended its statistics block after the last separator, so the final
    /// chunk is not a path.
    /// </param>
    /// <returns>The non-empty paths, as the engine emitted them.</returns>
    private static IEnumerable<string> Split(string output, bool trailing)
    {
        var parts = output.Split('\0');
        var count = trailing ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
            if (parts[i].Length > 0)
                yield return parts[i];
    }

    /// <summary>Turns the paths the engine emitted into vault paths, keeping those the caller may see.</summary>
    /// <param name="vault">The vault that was searched.</param>
    /// <param name="emitted">The paths the engine emitted.</param>
    /// <param name="admit">Decides which paths the caller may see.</param>
    /// <returns>The admitted paths, or a failure when the engine emitted something that is not a path.</returns>
    private static SearchOutcome<VaultPath> Paths(Vault vault, IEnumerable<string> emitted, Func<VaultPath, bool> admit)
    {
        var results = new List<VaultPath>();
        foreach (var text in emitted)
        {
            if (!VaultPath.TryCreate(vault, text, out var path, out var reason))
                return new SearchOutcome<VaultPath>.Failed($"The search engine emitted \"{text}\", which is not a path in this vault: {reason}");

            if (admit(path))
                results.Add(path);
        }

        return new SearchOutcome<VaultPath>.Results(results);
    }

    /// <summary>
    /// Parses the count output, which is <c>path\0count</c> per file, one per line, with the
    /// statistics block after the last of them. Reading it as a stream of NUL-separated fields
    /// keeps every path intact. A count and the next path share a chunk and are split at the
    /// newline between them.
    /// </summary>
    /// <param name="vault">The vault that was searched.</param>
    /// <param name="output">Everything the run wrote to standard output.</param>
    /// <param name="admit">Decides which paths the caller may see.</param>
    /// <returns>The admitted counts, or a failure when the output does not parse.</returns>
    private static SearchOutcome<MatchCount> Counted(Vault vault, string output, Func<VaultPath, bool> admit)
    {
        var parts = output.Split('\0');
        var results = new List<MatchCount>();
        var pending = parts[0];

        for (var i = 1; i < parts.Length; i++)
        {
            var chunk = parts[i];
            var newline = chunk.IndexOf('\n');
            var countText = newline < 0 ? chunk : chunk[..newline];

            if (!int.TryParse(countText, out var count))
                return new SearchOutcome<MatchCount>.Failed($"The search engine emitted \"{countText}\" where a count was expected.");

            if (!VaultPath.TryCreate(vault, pending, out var path, out var reason))
                return new SearchOutcome<MatchCount>.Failed($"The search engine emitted \"{pending}\", which is not a path in this vault: {reason}");

            if (admit(path))
                results.Add(new MatchCount(path, count));

            pending = newline < 0 ? string.Empty : chunk[(newline + 1)..];
        }

        return new SearchOutcome<MatchCount>.Results(results);
    }

    /// <summary>Parses the JSON output, keeping the match events for paths the caller may see.</summary>
    /// <param name="vault">The vault that was searched.</param>
    /// <param name="output">Everything the run wrote to standard output.</param>
    /// <param name="admit">Decides which paths the caller may see.</param>
    /// <returns>The admitted matching lines, or a failure when the output does not parse.</returns>
    private static SearchOutcome<MatchLine> Matches(Vault vault, string output, Func<VaultPath, bool> admit)
    {
        var results = new List<MatchLine>();

        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "match")
                continue;

            var data = root.GetProperty("data");
            var emitted = Text(data.GetProperty("path"));

            if (!VaultPath.TryCreate(vault, emitted, out var path, out var reason))
                return new SearchOutcome<MatchLine>.Failed($"The search engine emitted \"{emitted}\", which is not a path in this vault: {reason}");

            if (!admit(path))
                continue;

            results.Add(new MatchLine(
                path,
                data.GetProperty("line_number").GetInt32(),
                Text(data.GetProperty("lines")).TrimEnd('\n').TrimEnd('\r')));
        }

        return new SearchOutcome<MatchLine>.Results(results);
    }

    /// <summary>
    /// Reads a JSON node's text. The engine emits text it could not decode as UTF-8 as base64
    /// instead, and both forms are read here, so a note whose bytes are not valid UTF-8 still
    /// reports its matches rather than failing the whole search.
    /// </summary>
    /// <param name="node">A JSON node carrying either a <c>text</c> or a <c>bytes</c> property.</param>
    /// <returns>The node's text.</returns>
    private static string Text(JsonElement node) =>
        node.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : System.Text.Encoding.UTF8.GetString(node.GetProperty("bytes").GetBytesFromBase64());
}
