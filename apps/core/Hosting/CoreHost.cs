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
using System.Security.Cryptography.X509Certificates;
using Hispania.Core.Http;
using System.Threading.RateLimiting;
using Hispania.Runtime;
using Hispania.Core.Modules;

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
    private static readonly Action<ILogger, string, string, Exception?> HttpRequestFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(4, "CoreHttpRequestFailed"), "Core request failed with {ExceptionType}. CorrelationId {CorrelationId}.");
    private static readonly Action<ILogger, string, Exception?> LoginRateLimited = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(5, "CoreLoginRateLimited"), "Core login rate limit activated. CorrelationId {CorrelationId}.");

    internal static WebApplication Build(IReadOnlyDictionary<string, string?>? overrides = null, bool httpEnabled = true)
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
        builder.Services.AddModuleRuntime();
        builder.Services.AddSingleton<ModuleExtensionCatalog>(services =>
        {
            var publisherSection = builder.Configuration.GetSection("MODULES:TRUSTEDPUBLISHERKEYS");
            var publishers = publisherSection.GetChildren().ToDictionary(child => child.Key, child => child.Value ?? string.Empty, StringComparer.Ordinal);
            var revoked = builder.Configuration.GetSection("MODULES:REVOKEDPUBLISHERS").GetChildren()
                .Select(child => child.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
            var root = Path.Combine(hostOptions.Storage.DataDirectory, "modules");
            return new ModuleExtensionCatalog(root, new Version(1, 0, 0), services.GetRequiredService<IModuleRuntime>(),
                services.GetRequiredService<IModuleRegistry>(), new Dictionary<string, Func<Hispania.ModuleSystem.Contracts.IModule>>(),
                ["hispania.slice7.fixture"], trustedPublisherKeys: publishers, revokedPublisherIds: revoked,
                requirePublisherSignature: true,
                moduleHostPath: builder.Configuration["MODULES:MODULEHOSTPATH"]);
        });
        builder.Services.AddSingleton(services => new CoreModuleManager(
            services.GetRequiredService<ModuleExtensionCatalog>(), hostOptions.Storage.DataDirectory));
        builder.Services.AddHostedService<PackageLifecycleHostedService>();
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
        builder.Services.AddSingleton<ICoreHttpAuditService, CoreHttpAuditService>();
        builder.Services.AddSingleton<ICoreBootstrapService, CoreBootstrapService>();
        if (httpEnabled)
        {
            builder.Services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-Hispania-CSRF";
                options.Cookie.Name = "__Secure-HispaniaCsrf";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/api/v1/auth";
            });
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("core-login", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown-origin",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = hostOptions.LoginRateLimit,
                        Window = TimeSpan.FromSeconds(hostOptions.LoginRateWindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
                options.OnRejected = HandleRateLimitRejectedAsync;
            });
        }
        // IHostedLifecycleService.StartingAsync runs before any IHostedService.StartAsync,
        // including Kestrel's listener, so storage must pass before the host can accept traffic.
        builder.Services.AddHostedService<CorePersistenceLifecycle>();
        X509Certificate2? httpsCertificate = null;
        if (httpEnabled)
        {
            httpsCertificate = hostOptions.LoadHttpsCertificate(builder.Configuration["HTTPS_CERTIFICATE_PASSWORD"]);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 128L * 1024 * 1024;
                options.Listen(hostOptions.ListenAddress, hostOptions.Port, listen => listen.UseHttps(httpsCertificate));
            });
        }

        var app = builder.Build();
        if (httpEnabled)
        {
            app.Use(async (context, next) =>
            {
                context.TraceIdentifier = Guid.NewGuid().ToString("N");
                context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
                try { await next(context); }
                catch (Exception exception) when (!context.Response.HasStarted)
                {
                    HttpRequestFailed(app.Logger, exception.GetType().Name, context.TraceIdentifier, null);
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsJsonAsync(new CoreAuthEndpoints.ErrorResponse("internal_error"));
                }
            });
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await next(context);
            });
            app.Use(async (context, next) =>
            {
                if (!HasExpectedHost(context, hostOptions))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                await next(context);
            });
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                    feature.MaxRequestBodySize = context.Request.Path.Equals("/api/v1/auth/modules/install", StringComparison.OrdinalIgnoreCase)
                        ? 128L * 1024 * 1024 : 4096;
                await next(context);
            });
            app.UseRateLimiter();
            CoreAuthEndpoints.Map(app);
            CoreModuleEndpoints.Map(app);
            app.MapGet("/health/live", static () => Results.Json(new LiveStatus("healthy")))
                .WithName("CoreLiveness")
                .Produces<LiveStatus>(StatusCodes.Status200OK, "application/json");
            app.Lifetime.ApplicationStopped.Register(() => httpsCertificate!.Dispose());
        }

        app.Lifetime.ApplicationStarted.Register(() =>
            HostStarted(app.Logger, hostOptions.Version, hostOptions.EnvironmentName, hostOptions.ListenAddress, hostOptions.Port, null));
        app.Lifetime.ApplicationStopping.Register(() => HostStopping(app.Logger, null));
        app.Lifetime.ApplicationStopped.Register(() => HostStopped(app.Logger, null));
        return app;
    }

    private sealed record LiveStatus(string Status);

    private static bool HasExpectedHost(HttpContext context, CoreHostOptions options)
    {
        if (!context.Request.IsHttps || !context.Request.Host.HasValue ||
            !IPAddress.TryParse(context.Request.Host.Host, out var requestedAddress) ||
            !requestedAddress.Equals(options.ListenAddress))
            return false;

        var requestedPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        return requestedPort == options.Port;
    }

    private static async ValueTask HandleRateLimitRejectedAsync(Microsoft.AspNetCore.RateLimiting.OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out TimeSpan duration)
            ? Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))
            : 60;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Hispania.Core.Security");
        LoginRateLimited(logger, context.HttpContext.TraceIdentifier, null);
        try { await services.GetRequiredService<ICoreHttpAuditService>().RecordAsync("http.login.rate_limited", null, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HttpRequestFailed(logger, exception.GetType().Name, context.HttpContext.TraceIdentifier, null);
        }
    }
}
