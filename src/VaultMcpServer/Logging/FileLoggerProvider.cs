namespace VaultMcpServer.Logging;

/// <summary>
/// Writes every log line to one text file, named for the time the server started (for example
/// vault-mcp-server-20260718-093015123.log) under a gitignored logs directory.
/// </summary>
/// <remarks>
/// Program.cs registers this with <c>ILoggingBuilder.AddProvider</c>, so it takes part in the
/// normal ASP.NET Core logging pipeline like the built-in console provider. Category and level
/// filtering, including appsettings.json's "Logging" section, is applied by the framework's
/// <c>LoggerFactory</c> before a message reaches this provider, so there is no level handling
/// here. The implementation is a single <see cref="System.IO.StreamWriter"/> behind one lock,
/// with no per-request buffering, no rotation beyond one file per process, and no external
/// logging packages.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _writeLock = new();
    private bool _disposed;

    /// <summary>Creates the log directory if needed and opens this process's log file.</summary>
    /// <param name="logDirectory">The directory to write the log file into. Created if it does
    /// not already exist.</param>
    public FileLoggerProvider(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        // The file name carries milliseconds so that two server starts within the same second
        // do not produce the same name and collide on FileMode.Create. The test suite starts
        // several WebApplicationFactory<Program> fixtures in quick succession, each of which
        // constructs its own provider.
        var fileName = $"vault-mcp-server-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.log";
        var stream = new FileStream(Path.Combine(logDirectory, fileName), FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    /// <summary>Returns a logger that writes this category's messages to the log file.</summary>
    /// <param name="categoryName">The category the returned logger stamps on each line.</param>
    /// <returns>A logger bound to this provider.</returns>
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <summary>
    /// Appends one already-formatted line, under the single-writer lock. Does nothing once
    /// <see cref="Dispose"/> has run, so a logger call racing shutdown does not throw.
    /// </summary>
    /// <param name="line">The formatted line to append.</param>
    internal void WriteLine(string line)
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;
            _writer.WriteLine(line);
        }
    }

    /// <summary>Closes the log file. Subsequent writes are ignored.</summary>
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

    /// <summary>Formats messages for one category and hands them to the owning provider.</summary>
    /// <param name="categoryName">The category stamped on each line.</param>
    /// <param name="provider">The provider that owns the log file.</param>
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
