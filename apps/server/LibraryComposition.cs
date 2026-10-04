using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.Persistence.EFCore;
using Hispania.Metadata.Application;

namespace Hispania.Server;

public sealed class LibraryOptions
{
    public List<LibraryRootOptions> Roots { get; init; } = [];
}

public sealed class LibraryRootOptions
{
    public string Key { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

public sealed class LibraryReadiness
{
    public bool IsReady { get; private set; }
    public void MarkReady() => IsReady = true;
}

public sealed class PersistenceStartupService(ILibraryPersistenceInitializer initializer, EfLibraryCatalogStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => initializer.InitializeAsync(store, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class InitialLibraryScanService : IHostedService
{
    private readonly LibraryScanner scanner;
    private readonly LibraryReadiness readiness;
    private readonly EfLibraryCatalogStore? store;
    private readonly LibraryScanScope? scope;

    public InitialLibraryScanService(LibraryScanner scanner, LibraryReadiness readiness)
    { this.scanner = scanner; this.readiness = readiness; }
    public InitialLibraryScanService(LibraryScanner scanner, LibraryReadiness readiness, EfLibraryCatalogStore store, LibraryScanScope scope)
    { this.scanner = scanner; this.readiness = readiness; this.store = store; this.scope = scope; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await scanner.ScanAsync(cancellationToken);
        if (store is not null && scope is not null) await store.LoadSnapshotAsync(scope.RootKeys, cancellationToken);
        readiness.MarkReady();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class AutomaticMovieMetadataEnrichmentHostedService(MovieMetadataAutoEnrichmentService enrichment, LibraryReadiness readiness, ILogger<AutomaticMovieMetadataEnrichmentHostedService> logger, LibraryScanner scanner, EfLibraryCatalogStore catalogStore, LibraryScanScope scanScope, MovieMetadataIndexDocument indexDocument) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, Exception?> LogRunCompletedWithFailures = LoggerMessage.Define<int, int, int>(LogLevel.Error, new EventId(1, "AutomaticMetadataEnrichmentFailures"), "Automatic metadata enrichment completed with item failures. Processed={Processed} Conflicts={Conflicts} Failures={Failures}");
    private static readonly Action<ILogger, int, int, Exception?> LogRunCompleted = LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(2, "AutomaticMetadataEnrichmentCompleted"), "Automatic metadata enrichment completed. Processed={Processed} Conflicts={Conflicts}");
    private static readonly Action<ILogger, Exception?> LogRunAborted = LoggerMessage.Define(LogLevel.Critical, new EventId(3, "AutomaticMetadataEnrichmentAborted"), "Automatic metadata enrichment could not complete; the HTTP server remains available.");
    private static readonly Action<ILogger, Exception?> LogIndexWriteFailed = LoggerMessage.Define(LogLevel.Warning, new EventId(4, "MovieMetadataIndexWriteFailed"), "Could not refresh the movie metadata index document.");
    private readonly SemaphoreSlim runGate = new(1, 1);
    private int isRunning;
    public MovieMetadataEnrichmentRunResult? LastRun { get; private set; }
    public ManualLibraryMetadataScanResult? LastManualRun { get; private set; }
    public bool IsRunning => Volatile.Read(ref isRunning) != 0;

    public async Task<MovieMetadataEnrichmentRunResult?> RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await runGate.WaitAsync(0, cancellationToken)) return null;
        Volatile.Write(ref isRunning, 1);
        try
        {
            LastRun = await enrichment.EnrichCatalogAsync(cancellationToken);
            await WriteIndexAsync(cancellationToken);
            if (LastRun.Failures > 0) LogRunCompletedWithFailures(logger, LastRun.ProcessedItems, LastRun.Conflicts, LastRun.Failures, null);
            else LogRunCompleted(logger, LastRun.ProcessedItems, LastRun.Conflicts, null);
            return LastRun;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            LastRun = new MovieMetadataEnrichmentRunResult(0, 0, 1, []);
            LogRunAborted(logger, null);
            return LastRun;
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
            runGate.Release();
        }
    }

    public async Task<ManualLibraryMetadataScanResult?> ScanAllLibrariesNowAsync(CancellationToken cancellationToken = default)
        => await ScanLibrariesNowAsync(refreshExistingMetadata: true, cancellationToken);

    public async Task<ManualLibraryMetadataScanResult?> ScanNewLibrariesNowAsync(CancellationToken cancellationToken = default)
        => await ScanLibrariesNowAsync(refreshExistingMetadata: false, cancellationToken);

    private async Task<ManualLibraryMetadataScanResult?> ScanLibrariesNowAsync(bool refreshExistingMetadata, CancellationToken cancellationToken)
    {
        if (!await runGate.WaitAsync(0, cancellationToken)) return null;
        Volatile.Write(ref isRunning, 1);
        try
        {
            var library = await scanner.ScanAsync(cancellationToken);
            await catalogStore.LoadSnapshotAsync(scanScope.RootKeys, cancellationToken);
            LastRun = refreshExistingMetadata
                ? await enrichment.RefreshEntireCatalogAsync(cancellationToken)
                : await enrichment.EnrichUnidentifiedCatalogAsync(cancellationToken);
            await WriteIndexAsync(cancellationToken);
            LastManualRun = new ManualLibraryMetadataScanResult(library, LastRun);
            if (LastRun.Failures > 0) LogRunCompletedWithFailures(logger, LastRun.ProcessedItems, LastRun.Conflicts, LastRun.Failures, null);
            else LogRunCompleted(logger, LastRun.ProcessedItems, LastRun.Conflicts, null);
            return LastManualRun;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            LogRunAborted(logger, null);
            throw;
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
            runGate.Release();
        }
    }

    private async Task WriteIndexAsync(CancellationToken cancellationToken)
    {
        try { await indexDocument.WriteAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { LogIndexWriteFailed(logger, null); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!readiness.IsReady && !stoppingToken.IsCancellationRequested) await Task.Delay(50, stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;
        try { await RunNowAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public sealed record ManualLibraryMetadataScanResult(LibraryScanResult Library, MovieMetadataEnrichmentRunResult Metadata);
