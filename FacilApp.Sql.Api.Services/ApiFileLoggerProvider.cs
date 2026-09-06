using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace FacilApp.Sql.Api.Services;

/// <summary>
/// Registro físico da API. Não grava corpos de requisição/resposta nem cabeçalhos,
/// evitando que Bearer, senhas e dados pessoais sejam persistidos no log.
/// </summary>
public sealed class ApiFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;

    public ApiFileLoggerProvider(string directory, LogLevel minimumLevel)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        new ApiFileLogger(_directory, _minimumLevel, categoryName);

    public void Dispose()
    {
    }

    private sealed class ApiFileLogger : ILogger
    {
        private static readonly object Sync = new();
        private readonly string _directory;
        private readonly LogLevel _minimumLevel;
        private readonly string _category;

        public ApiFileLogger(string directory, LogLevel minimumLevel, string category)
        {
            _directory = directory;
            _minimumLevel = minimumLevel;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _minimumLevel;

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

            try
            {
                string file = Path.Combine(
                    _directory,
                    "API_FACILAPP_SQL_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".LOG");
                string message = formatter(state, exception);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category} - {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                lock (Sync)
                {
                    File.AppendAllText(file, line + Environment.NewLine, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch
            {
                // O log jamais pode derrubar uma chamada da API.
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
