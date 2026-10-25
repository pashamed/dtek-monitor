using System.Collections.Concurrent;
using DtekBot.Shared;

namespace DtekBot.Api;

// Minimal daily-rolling file logger, because IIS does not expose the console of an in-process app.
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, Write));

    public void Dispose() => _loggers.Clear();

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"app-{KyivTime.FromUtc(DateTimeOffset.UtcNow):yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(string category, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Kyiv time with its offset, e.g. 2026-10-25T20:44:47.988+02:00.
            var line = $"{KyivTime.FromUtc(DateTimeOffset.UtcNow):yyyy-MM-ddTHH:mm:ss.fffzzz} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            write(line);
        }
    }
}
