using System.Text.Json;

namespace VaultShared.Access;

/// <summary>
/// How the engine is driven: the arguments each surface is asked for, the classification of what
/// came back, and the parse of the shapes the engine emits. The scope, dot-rule and markdown
/// predicates are the caller's - <see cref="VaultAccess"/> composes them and hands one in - because
/// they are properties of the caller's authority rather than of the engine.
///
/// Every surface here invokes the engine once and reads both its streams whole before answering.
/// </summary>
internal static class VaultSearch
{
    /// <summary>
    /// What every invocation carries. <c>--no-config</c> because the engine otherwise takes options
    /// from a file named by an environment variable, which is exactly the ambient state nothing
    /// here reads. <c>--hidden</c> because the engine's hidden-file skipping is not the dot rule -
    /// hidden on Windows is a file attribute, which a dot-prefixed directory does not carry, so
    /// leaving it on would drop dot entries the census must see while still admitting hidden ones.
    /// <c>--no-ignore</c> because a gitignored file is still on disk and still destroyed, so no
    /// ignore-file behaviour is inherited.
    /// </summary>
    private static readonly string[] Always = ["--no-config", "--hidden", "--no-ignore"];

    /// <summary>
    /// The operand every invocation ends with, naming the working directory the engine was started
    /// in - the vault root, which is what makes every emitted path vault-relative. Stated rather
    /// than left implicit: with nothing to search named, the engine searches standard input whenever
    /// standard input is redirected, and a process started as a child always has it redirected. The
    /// answer would then be a search of nothing, reported as a successful empty result.
    /// </summary>
    private static readonly string[] Here = ["--", "."];

    /// <summary>
    /// Every file the engine walks, filtered by <paramref name="admit"/>. Both enumeration surfaces
    /// come through here; what separates them is the predicate.
    /// </summary>
    internal static SearchOutcome<VaultPath> Enumerate(
        Vault vault, SearchEngine engine, string? pattern, Func<VaultPath, bool> admit)
    {
        var run = engine.Run(vault.Root, [.. Always, .. Glob(pattern), "--files", "--null", .. Here]);
        return Read(run, vault, output => Paths(vault, Split(output, trailing: false), admit));
    }

    /// <summary>Matching lines with their locations: the richest granularity.</summary>
    internal static SearchOutcome<MatchLine> Lines(
        Vault vault, SearchEngine engine, string regex, string? pattern, Func<VaultPath, bool> admit)
    {
        // --json is the one output shape that carries the path, the line number and the line's text
        // as separate fields, so none of them has to be recovered from a rendering that a path
        // holding a colon would make ambiguous. Its summary event doubles as the completion marker
        // Read below turns on.
        var run = engine.Run(vault.Root, [.. Always, .. Glob(pattern), "--json", "--regexp", regex, .. Here]);
        return Read(run, vault, output => Matches(vault, output, admit));
    }

    /// <summary>
    /// The set of files containing a match. Its own invocation rather than the line granularity with
    /// the lines dropped: a caller wanting a file list must not pay to materialize every match, and
    /// on a whole-vault search that difference is the whole cost of the operation.
    /// </summary>
    internal static SearchOutcome<VaultPath> Containing(
        Vault vault, SearchEngine engine, string regex, string? pattern, Func<VaultPath, bool> admit)
    {
        var run = engine.Run(
            vault.Root, [.. Always, .. Glob(pattern), "--files-with-matches", "--null", "--stats", "--regexp", regex, .. Here]);
        return Read(run, vault, output => Paths(vault, Split(output, trailing: true), admit));
    }

    /// <summary>How many lines matched per file, again as its own invocation for the same reason.</summary>
    internal static SearchOutcome<MatchCount> Counts(
        Vault vault, SearchEngine engine, string regex, string? pattern, Func<VaultPath, bool> admit)
    {
        var run = engine.Run(
            vault.Root, [.. Always, .. Glob(pattern), "--count", "--null", "--stats", "--regexp", regex, .. Here]);
        return Read(run, vault, output => Counted(vault, output, admit));
    }

    /// <summary>
    /// The caller's pattern reaches the engine as <c>--iglob</c>, which is what supplies both
    /// <c>**</c> and case-insensitivity, and whose anchoring is the required one: a pattern with no
    /// separator matches a path's final segment at any depth, one with a separator is anchored at
    /// the vault root.
    /// </summary>
    private static string[] Glob(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? [] : ["--iglob", pattern];

    /// <summary>
    /// The four outcomes, decided from the run rather than from message text wherever it can be.
    ///
    /// The engine refuses a malformed glob or regex before it walks anything, so such a run produces
    /// no output at all; every other exit tells us it ran - it either completed (0 or 1) or it
    /// completed and also reported something (2, with the output it had already produced). That is
    /// the whole discriminator between a caller's bad pattern and a failure during the search, and
    /// it is why the search granularities ask for a completion marker they do not otherwise need:
    /// the JSON summary event and the <c>--stats</c> block are printed even when the run also
    /// failed, so "produced no output" cannot be confused with "matched nothing".
    ///
    /// Anything on the error stream fails the call whole. A search that cannot read a file must not
    /// answer with the files it could read: that turns a permissions problem into a wrong answer the
    /// caller cannot tell from a correct one.
    /// </summary>
    private static SearchOutcome<TResult> Read<TResult>(
        EngineRun run, Vault vault, Func<string, SearchOutcome<TResult>> parse)
    {
        if (run.ExitCode == SearchEngine.LaunchFailed)
            return new SearchOutcome<TResult>.Failed(run.StandardError);

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
    /// The entry a diagnostic is about, when it is about one. The engine attributes a failure it
    /// hit during the walk to the entry it hit it on - <c>rg: &lt;path&gt;: &lt;reason&gt;</c> - and
    /// says nothing that reads as a path when the failure is its own. Paths here are already
    /// vault-relative, the engine having been rooted at the vault directory.
    /// </summary>
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
    /// NUL-separated paths, so a path holding a colon, a space or a newline still parses. With
    /// <paramref name="trailing"/> the engine appends its statistics after the last separator, and
    /// that remainder is not a path.
    /// </summary>
    private static IEnumerable<string> Split(string output, bool trailing)
    {
        var parts = output.Split('\0');
        var count = trailing ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
            if (parts[i].Length > 0)
                yield return parts[i];
    }

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
    /// <c>path\0count</c> per file, one per line, with the statistics block after the last of them.
    /// Reading it as a stream of NUL-separated fields keeps every path intact: the count and the
    /// next path share a chunk, split at the newline between them.
    /// </summary>
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
    /// The engine states text it could not decode as UTF-8 in base64 instead. Both forms are read
    /// here, so a note whose bytes are not valid UTF-8 still reports its matches rather than
    /// failing the search over content nothing was going to write back.
    /// </summary>
    private static string Text(JsonElement node) =>
        node.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : System.Text.Encoding.UTF8.GetString(node.GetProperty("bytes").GetBytesFromBase64());
}
