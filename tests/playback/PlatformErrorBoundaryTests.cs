using System.Collections.Concurrent;
using System.Net;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class PlatformErrorBoundaryTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("authorization")]
    [InlineData("events")]
    [InlineData("jobs")]
    [InlineData("storage")]
    [InlineData("worker")]
    public async Task ProviderFailuresNeverExposeExceptionMaterialInResponsesOrLogs(string subsystem)
    {
        var capture = new CaptureLogger();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(capture);
        await using var app = builder.Build(); app.UsePlatformErrorBoundary();
        app.MapGet("/failure", () => Task.FromException<string>(new IOException(
            $"{subsystem}: password=closure-secret; Authorization: Bearer closure-secret; /internal/root; SELECT secret FROM credentials; connection-string; private-key; raw-payload")));
        await app.StartAsync();
        using var client = app.GetTestClient(); using var response = await client.GetAsync("/failure");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Request could not be completed.", body);
        var logs = string.Join('\n', capture.Messages);
        Assert.Contains("Platform request failed (IOException)", logs);
        foreach (var marker in new[] { "closure-secret", "/internal/root", "SELECT secret", "connection-string", "private-key", "raw-payload", "StackTrace" })
        {
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(marker, logs, StringComparison.OrdinalIgnoreCase);
        }
        await app.StopAsync();
    }

    internal sealed class CaptureLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception) + exception?.ToString());
    }
}
