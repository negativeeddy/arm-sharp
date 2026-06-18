using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// ILoggerProvider that writes log messages to per-job log files.
/// Uses ILogger.BeginScope with "LogFilePath" key — fully thread-safe,
/// supports multiple concurrent jobs and parallel operations.
/// </summary>
public sealed class JobFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new();
    private IExternalScopeProvider? _scopeProvider;

    void ISupportExternalScope.SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider;

    public ILogger CreateLogger(string categoryName)
    {
        var logger = new JobFileLogger(this, categoryName);
        if (_scopeProvider is not null)
            logger.SetScopeProvider(_scopeProvider);
        return logger;
    }

    /// <summary>Scope key used to pass the log file path. Use with logger.BeginScope().</summary>
    public const string LogFilePathKey = "LogFilePath";

    internal StreamWriter GetWriter(string filePath)
    {
        return _writers.GetOrAdd(filePath, p =>
        {
            var dir = Path.GetDirectoryName(p);
            if (dir is not null) Directory.CreateDirectory(dir);
            var sw = new StreamWriter(p, append: true) { AutoFlush = true };
            sw.WriteLine();
            sw.WriteLine($"=== ARM Job Log: {Path.GetFileNameWithoutExtension(p)} ===");
            sw.WriteLine();
            sw.Flush();
            return sw;
        });
    }

    /// <summary>Remove and close the writer for a specific log file path.</summary>
    internal void RemoveWriter(string filePath)
    {
        if (_writers.TryRemove(filePath, out var sw))
        {
            try { sw.Flush(); sw.Dispose(); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        foreach (var w in _writers.Values) { w.Flush(); w.Dispose(); }
        _writers.Clear();
    }
}

internal sealed class JobFileLogger : ILogger
{
    private readonly JobFileLoggerProvider _provider;
    private readonly string _categoryName;
    private IExternalScopeProvider? _scopeProvider;

    public JobFileLogger(JobFileLoggerProvider provider, string categoryName)
    {
        _provider = provider;
        _categoryName = categoryName;
    }

    internal void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _scopeProvider?.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var msg = formatter(state, exception);
        var entry = $"[{DateTime.Now:HH:mm:ss}] {logLevel.ToString()[..4]}: {_categoryName}: {msg}";
        if (exception is not null) entry += $"\n{exception}";

        // Find the log file path from the current scope chain
        string? filePath = null;
        _scopeProvider?.ForEachScope<object?>((scope, _) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object>> props)
            {
                foreach (var kv in props)
                {
                    if (kv.Key == JobFileLoggerProvider.LogFilePathKey && kv.Value is string path)
                        filePath = path;
                }
            }
        }, null);

        if (filePath is null) return;

        var writer = _provider.GetWriter(filePath);
        lock (writer)
        {
            writer.WriteLine(entry);
        }
    }
}
