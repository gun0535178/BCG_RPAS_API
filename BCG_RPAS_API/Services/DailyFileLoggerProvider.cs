using System.Text;

namespace BCG_RPAS_API.Services
{
    public sealed class DailyFileLoggerProvider : ILoggerProvider
    {
        private readonly string _logDirectory;
        private readonly string _filePrefix;
        private readonly object _writeLock = new();

        public DailyFileLoggerProvider(string logDirectory, string filePrefix = "api")
        {
            _logDirectory = logDirectory;
            _filePrefix = filePrefix;
            Directory.CreateDirectory(_logDirectory);
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new DailyFileLogger(categoryName, _logDirectory, _filePrefix, _writeLock);
        }

        public void Dispose()
        {
        }

        private sealed class DailyFileLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly string _logDirectory;
            private readonly string _filePrefix;
            private readonly object _writeLock;

            public DailyFileLogger(string categoryName, string logDirectory, string filePrefix, object writeLock)
            {
                _categoryName = categoryName;
                _logDirectory = logDirectory;
                _filePrefix = filePrefix;
                _writeLock = writeLock;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel)
            {
                if (logLevel == LogLevel.None)
                {
                    return false;
                }

                // กรอง Noise ของระบบ Framework (Microsoft.*, System.*) ออก ยกเว้นเกิด Error วิกฤต
                if (_categoryName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                    _categoryName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                {
                    return logLevel >= LogLevel.Error;
                }

                // บันทึกเฉพาะ Log ของระบบเราเอง (BCG_RPAS_API.*) ตั้งแต่ระดับ Information ขึ้นไป
                return logLevel >= LogLevel.Information;
            }

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

                var now = DateTime.Now;
                var message = formatter(state, exception);
                var levelShort = logLevel switch
                {
                    LogLevel.Information => "INFO",
                    LogLevel.Warning => "WARN",
                    LogLevel.Error => "ERROR",
                    LogLevel.Critical => "CRIT",
                    _ => logLevel.ToString().ToUpperInvariant()
                };

                var sb = new StringBuilder();
                sb.AppendLine($"[{now:dd/MM/yyyy HH:mm:ss.fff}] [{levelShort}] {message}");

                if (exception != null)
                {
                    sb.AppendLine($"  EXCEPTION: {exception}");
                }

                sb.AppendLine(new string('-', 95));

                var filePath = Path.Combine(_logDirectory, $"{_filePrefix}-{now:yyyy-MM-dd}.log");

                lock (_writeLock)
                {
                    try
                    {
                        File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
                    }
                    catch
                    {
                        // Ignore file I/O contention errors
                    }
                }
            }
        }
    }
}
