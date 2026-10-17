using System.Net;
using Hispania.Core.Configuration;
using Hispania.Core.Identity;
using Hispania.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;

namespace Hispania.Core.Hosting;

internal static class CoreHost
{
    private static readonly Action<ILogger, string, string, IPAddress, int, Exception?> HostStarted =
        LoggerMessage.Define<string, string, IPAddress, int>(LogLevel.Information, new EventId(1, "CoreHostStarted"),
            "Core host started. Version {Version}; environment {Environment}; listener {Address}:{Port}.");
    private static readonly Action<ILogger, Exception?> HostStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, "CoreHostStopping"), "Core host stopping.");
    private static readonly Action<ILogger, Exception?> HostStopped =
        LoggerMessage.Define(LogLevel.Information, new EventId(3, "CoreHostStopped"), "Core host stopped.");

    internal static WebApplication Build(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(CoreHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Keep Core configuration deliberately small and prevent generic URL/Kestrel settings
        // from silently widening the listener beyond the validated loopback address.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables(prefix: "HISPANIA_CORE_");
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);

        var hostOptions = CoreHostOptions.Read(builder.Configuration, builder.Environment.EnvironmentName);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(hostOptions.MinimumLogLevel);
        builder.Logging.AddJsonConsole();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = hostOptions.ShutdownTimeout);
        builder.Services.AddSingleton(hostOptions.Storage);
        builder.Services.AddSingleton<ICorePersistence, SqliteCorePersistence>();
        builder.Services.AddSingleton<SqliteCoreIdentityStore>();
        builder.Services.AddSingleton<ICoreIdentityStore>(services => services.GetRequiredService<SqliteCoreIdentityStore>());
        builder.Services.AddSingleton<IPasswordHasher<CoreIdentity>, PasswordHasher<CoreIdentity>>();
        builder.Services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = CoreIdentityService.Iterations;
        });
        builder.Services.AddSingleton<CoreIdentityService>();
        builder.Services.AddSingleton<ICoreIdentityService>(services => services.GetRequiredService<CoreIdentityService>());
        builder.Services.AddSingleton<ICoreAuthorizationService, CoreAuthorizationService>();
        builder.Services.AddSingleton<ICoreSessionService, CoreSessionService>();
        builder.Services.AddSingleton<ICoreBootstrapService, CoreBootstrapService>();
        // IHostedLifecycleService.StartingAsync runs before any IHostedService.StartAsync,
        // including Kestrel's listener, so storage must pass before the host can accept traffic.
        builder.Services.AddHostedService<CorePersistenceLifecycle>();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(hostOptions.ListenAddress, hostOptions.Port);
        });

        var app = builder.Build();
        app.MapGet("/health/live", static () => Results.Json(new LiveStatus("healthy")))
            .WithName("CoreLiveness")
            .Produces<LiveStatus>(StatusCodes.Status200OK, "application/json");

        app.Lifetime.ApplicationStarted.Register(() =>
            HostStarted(app.Logger, hostOptions.Version, hostOptions.EnvironmentName, hostOptions.ListenAddress, hostOptions.Port, null));
        app.Lifetime.ApplicationStopping.Register(() => HostStopping(app.Logger, null));
        app.Lifetime.ApplicationStopped.Register(() => HostStopped(app.Logger, null));
        return app;
    }

    private sealed record LiveStatus(string Status);
}
