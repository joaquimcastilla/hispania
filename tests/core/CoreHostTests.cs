using System.Net;
using System.Net.Sockets;
using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class CoreHostTests : IDisposable
{
    private readonly List<string> _temporaryDataDirectories = [];

    [Fact]
    public void DefaultsUseLoopbackAndHaveNoDomainOrCredentialConfiguration()
    {
        var options = CoreHostOptions.Read(new ConfigurationBuilder().Build(), "Production");
        Assert.Equal(IPAddress.Loopback, options.ListenAddress);
        Assert.Equal(5070, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ShutdownTimeout);
        Assert.Equal(LogLevel.Information, options.MinimumLogLevel);
        Assert.False(string.Equals(options.Storage.DataDirectory, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(options.Version));
        Assert.DoesNotContain(typeof(CoreHost).Assembly.GetReferencedAssemblies(), reference =>
            new[] { "Hispania.Server", "Hispania.Inventory", "Hispania.Media", "Hispania.WorkerAgent", "Npgsql" }
                .Contains(reference.Name, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.50")]
    [InlineData("not-an-ip")]
    public void NonLoopbackOrInvalidAddressFailsClosed(string address)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LISTEN_ADDRESS"] = address }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidPortFailsConfiguration(string port)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PORT"] = port }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("invalid")]
    public void InvalidShutdownTimeoutFailsConfiguration(string timeout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SHUTDOWN_TIMEOUT_SECONDS"] = timeout }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("invalid")]
    public void InvalidLogLevelFailsConfiguration(string logLevel)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LOG_LEVEL"] = logLevel }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Fact]
    public void LogLevelCanBeConfiguredExplicitly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LOG_LEVEL"] = "Debug" }).Build();
        Assert.Equal(LogLevel.Debug, CoreHostOptions.Read(config, "Development").MinimumLogLevel);
    }

    [Fact]
    public async Task OnlyExplicitLivenessGetIsPublicAndReturnsStableGenericStatus()
    {
        var port = GetAvailableLoopbackPort();
        await using var app = Build(port);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"healthy\"}", json);
        Assert.DoesNotContain("admin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);

        using var post = await client.PostAsync("/health/live", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var admin = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);

        await app.StopAsync();
        Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationStopsTheHostCleanly()
    {
        var port = GetAvailableLoopbackPort();
        await using var app = Build(port);
        using var cancellation = new CancellationTokenSource();
        await app.StartAsync();
        var run = app.WaitForShutdownAsync(cancellation.Token);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var response = await WaitForHealthAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task OccupiedPortFailsStartupWithoutStartingTheHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var app = Build(port);
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    [Fact]
    public void HealthRouteIsTheOnlyMappedApplicationEndpoint()
    {
        using var app = CoreHost.Build(new Dictionary<string, string?> { ["PORT"] = "5070" });
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Single(endpoints);
        Assert.Equal("/health/live", Assert.IsType<Microsoft.AspNetCore.Routing.RouteEndpoint>(endpoints[0]).RoutePattern.RawText);
    }

    private WebApplication Build(int port)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "hispania-core-host-tests", Guid.NewGuid().ToString("N"));
        _temporaryDataDirectories.Add(dataDirectory);
        return CoreHost.Build(new Dictionary<string, string?>
        {
            ["LISTEN_ADDRESS"] = "127.0.0.1",
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SHUTDOWN_TIMEOUT_SECONDS"] = "3",
            ["DATA_DIRECTORY"] = dataDirectory
        });
    }

    public void Dispose()
    {
        foreach (var directory in _temporaryDataDirectories)
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<HttpResponseMessage> WaitForHealthAsync(HttpClient client)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            try { return await client.GetAsync("/health/live"); }
            catch (HttpRequestException) { await Task.Delay(50); }
        }
        throw new TimeoutException("Core liveness endpoint did not start in time.");
    }
}
