using Microsoft.Extensions.Logging;

namespace SyncMax.Logging;

/// <summary>
/// Простой провайдер логирования в файл для <c>Microsoft.Extensions.Logging</c> — без внешних
/// зависимостей. Работает параллельно со стандартным консольным провайдером (то же сообщение
/// уходит и в консоль, и в файл). Файлы складываются в подпапку <c>logs</c> рядом с бинарником
/// с ротацией по дням: <c>syncmax-YYYY-MM-DD.log</c>. Уровни берутся из общей конфигурации
/// логирования (<c>Logging:LogLevel</c>); отдельно можно настроить через алиас <c>File</c>.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private DateOnly _currentDate;

    public FileLoggerProvider(string directory, string prefix = "syncmax")
    {
        _directory = directory;
        _prefix = prefix;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Потокобезопасная запись строки; при смене суток переключается на новый файл.</summary>
    internal void Write(string line)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || today != _currentDate)
            {
                _writer?.Dispose();
                _currentDate = today;
                var path = Path.Combine(_directory, $"{_prefix}-{today:yyyy-MM-dd}.log");
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream) { AutoFlush = true };
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        // Область видимости не используем; фильтрацию по уровню делает LoggerFactory до вызова Log.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{ShortLevel(logLevel)}] {_category}: {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            _provider.Write(line);
        }

        private static string ShortLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
