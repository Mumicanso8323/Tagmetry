using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Tagmetry.Adapters.Logging;

public sealed class FileLoggerProvider : ILoggerProvider {
    private readonly string _filePath;
    private readonly object _gate = new();
    private bool _disposed;

    public FileLoggerProvider(string filePath) {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void WriteLine(string line) {
        if (_disposed) return;
        lock (_gate) {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    public void Dispose() => _disposed = true;

    private sealed class FileLogger : ILogger {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category) {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            var msg = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {_category}: {msg}";
            if (exception != null) line += Environment.NewLine + exception;
            _provider.WriteLine(line);
        }

        private sealed class NullScope : IDisposable {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
