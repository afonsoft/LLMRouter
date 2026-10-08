using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LLMRouter.Server.Services;

/// <summary>
/// Ring buffer of server log lines (mirrors upstream consoleLogBuffer):
/// an ILoggerProvider that captures every log entry; consumers poll
/// <see cref="Recent"/> or subscribe to <see cref="Stream"/> for live tail.
/// </summary>
public sealed class ConsoleLogBuffer : ILoggerProvider
{
    public static readonly ConsoleLogBuffer Instance = new();
    private const int Capacity = 500;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

    private ConsoleLogBuffer() { }

    public IEnumerable<string> Recent(int count = 200) => _lines.Reverse().Take(count).Reverse();

    public ChannelReader<string> Stream => _channel.Reader;

    public void Add(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Capacity && _lines.TryDequeue(out _)) { }
        _channel.Writer.TryWrite(line);
    }

    public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName);

    public void Dispose() { }

    private sealed class BufferLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(msg)) return;
            var short_cat = category.Split('.').Last();
            Instance.Add($"[{DateTime.Now:HH:mm:ss}] {logLevel.ToString()[..4].ToUpper()} {short_cat}: {msg}" +
                (exception is null ? "" : $" {exception.GetType().Name}: {exception.Message}"));
        }
    }
}
