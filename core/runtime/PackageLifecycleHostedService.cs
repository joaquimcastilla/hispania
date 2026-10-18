using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hispania.Runtime;

/// <summary>Revalidates optional extension sets after the statically composed runtime starts.</summary>
public sealed class PackageLifecycleHostedService(ModuleExtensionCatalog catalog, ILogger<PackageLifecycleHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogBlocked = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1901, "PackageGraphBlocked"),
        "Package graph remains blocked with failure type {FailureType}; Core will continue without activating it.");
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await catalog.RecoverPackageSetAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogBlocked(logger, exception.GetType().Name, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => catalog.StopManagedPackagesAsync(cancellationToken);
}
