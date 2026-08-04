namespace VaultMcpServer.Logging;

/// <summary>
/// Hand-rolled <see cref="ILoggerProvider"/> that writes every log line to a single text
/// file whose name is timestamped to server start (e.g. vault-mcp-server-20260718-093015.log)
/// under a gitignored logs/ directory. Registered into the standard logging builder in
/// Program.cs via <c>ILoggingBuilder.AddProvider</c>, so it participates in the normal
/// ASP.NET Core logging pipeline exactly like the built-in console provider: category and
/// level filtering (including appsettings.json's "Logging" section) is applied by the
/// framework's <c>LoggerFactory</c> before a message ever reaches this provider - there is
/// no bespoke level machinery here.
///
/// Deliberately simple: a single writer behind one lock (no per-request
/// buffering, no rolling/rotation beyond the one file per process lifetime), and no
/// external logging packages - only <see cref="System.IO.StreamWriter"/> and framework
/// logging abstractions.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _writeLock = new();
    private bool _disposed;

    /// <param name="logDirectory">Created if it doesn't already exist.</param>
    public FileLoggerProvider(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        // Millisecond resolution (rather than the whole-second precision the design
        // sketch's example name implied) so two server starts within the same wall-clock
        // second - e.g. the test suite's several WebApplicationFactory<Program> fixtures,
        // which each boot a real Program and so each construct their own provider in quick
        // succession - don't generate the same file name and collide on FileMode.Create.
        var fileName = $"vault-mcp-server-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.log";
        var stream = new FileStream(Path.Combine(logDirectory, fileName), FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <summary>Appends one already-formatted line, under the single-writer lock. A no-op
    /// once <see cref="Dispose"/> has run, so a logger call racing shutdown doesn't throw.</summary>
    internal void WriteLine(string line)
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {categoryName}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.WriteLine(line);
        }
    }
}
