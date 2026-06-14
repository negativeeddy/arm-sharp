using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// Singleton ILoggerProvider that writes to per-job log files via AsyncLocal.
/// All loggers in the same async context automatically write to the right file.
/// </summary>
public sealed class JobFileLoggerProvider : ILoggerProvider
{
    private static readonly AsyncLocal<string?> _currentLogPath = new();
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new();

    /// <summary>
    /// Set the log file path for the current async context.
    /// Returns an IDisposable that restores the previous path when disposed.
    /// </summary>
    public static IDisposable BeginJobScope(string logFilePath)
    {
        var previous = _currentLogPath.Value;
        _currentLogPath.Value = logFilePath;
        return new ScopeRestorer(previous);
    }

    public ILogger CreateLogger(string categoryName) => new JobFileLogger(this, categoryName);

    internal void Write(string categoryName, string entry)
    {
        var path = _currentLogPath.Value;
        if (path is null) return;

        var writer = _writers.GetOrAdd(path, p =>
        {
            var dir = System.IO.Path.GetDirectoryName(p);
            if (dir is not null) System.IO.Directory.CreateDirectory(dir);
            var sw = new StreamWriter(p, append: true) { AutoFlush = true };
            sw.WriteLine();
            sw.WriteLine(string.Concat("=== ARM Job Log: ", System.IO.Path.GetFileNameWithoutExtension(p), " ==="));
            sw.WriteLine();
            sw.Flush();
            return sw;
        });

        lock (writer)
        {
            writer.WriteLine(entry);
        }
    }

    public void Dispose()
    {
        foreach (var w in _writers.Values) { w.Flush(); w.Dispose(); }
        _writers.Clear();
    }

    private sealed class ScopeRestorer(string? previous) : IDisposable
    {
        public void Dispose() => _currentLogPath.Value = previous;
    }
}

internal sealed class JobFileLogger(JobFileLoggerProvider provider, string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        var entry = string.Concat("[", DateTime.Now.ToString("HH:mm:ss"), "] ", logLevel.ToString()[..4], ": ", categoryName, ": ", msg);
        if (exception is not null) entry = string.Concat(entry, "\n", exception.ToString());
        provider.Write(categoryName, entry);
    }
}
