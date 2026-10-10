using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hispania.Runtime;

/// <summary>Revalidates optional extension sets after the statically composed runtime starts.</summary>
public sealed class PackageLifecycleHostedService(ModuleExtensionCatalog catalog, ILogger<PackageLifecycleHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogBlocked = LoggerMessage.Define(LogLevel.Warning, new EventId(1901, "PackageGraphBlocked"),
        "Package graph remains blocked; inspect package diagnostics before enabling extensions.");
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await catalog.RecoverPackageSetAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { LogBlocked(logger, null); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => catalog.StopManagedPackagesAsync(cancellationToken);
}
