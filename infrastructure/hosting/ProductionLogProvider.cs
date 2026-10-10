using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hispania.Hosting;

// Service output goes to journald/stdout and a local file on Windows. Exceptions
// are never formatted: database/client exceptions may embed private connection data.
public sealed class ProductionLogProvider(string path, IEnumerable<string> sensitiveValues) : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string[] secrets = sensitiveValues.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
    public ILogger CreateLogger(string categoryName) => new ProductionLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, bool failed)
    {
        foreach (var secret in secrets) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (message.Length > 8192) message = message[..8192];
        var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), category, level = level.ToString(), message, exception = failed ? "details_suppressed" : null });
        lock (gate)
        {
            Console.WriteLine(line);
            if (OperatingSystem.IsWindows()) File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class ProductionLogger(ProductionLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, null), exception is not null);
        }
    }
}
