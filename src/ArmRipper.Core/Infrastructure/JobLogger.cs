using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

public sealed class JobLogger : ILogger
{
    private readonly string _jobId;
    private readonly string _logPath;
    private readonly StreamWriter _fileWriter;
    private readonly ILogger _inner;

    public JobLogger(string jobId, string logDirectory, ILogger inner)
    {
        _jobId = jobId;
        _inner = inner;
        _logPath = Path.Combine(logDirectory, $"arm_job_{jobId}.log");
        Directory.CreateDirectory(logDirectory);
        _fileWriter = new StreamWriter(_logPath, append: true) { AutoFlush = true };
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
        _fileWriter.WriteLine(line);
        _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Dispose()
    {
        _fileWriter.Flush();
        _fileWriter.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _fileWriter.FlushAsync();
        await _fileWriter.DisposeAsync();
    }
}
