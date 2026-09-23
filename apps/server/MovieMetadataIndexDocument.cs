using System.Text.Json;
using Hispania.Library.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;

namespace Hispania.Server;

/// <summary>Exports a human-readable view of the catalog and confirmed provider identifiers; SQLite remains authoritative.</summary>
public sealed class MovieMetadataIndexDocument(string? path, IMediaCatalogReader catalog, IMetadataEnrichmentStore metadataStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task WriteAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var snapshot = await metadataStore.LoadAsync(cancellationToken);
        var movies = catalog.GetAll().OrderBy(x => x.File.RootKey, StringComparer.Ordinal).ThenBy(x => x.File.RelativePath, StringComparer.Ordinal)
            .Select(entry => new MovieIndexEntry(
                entry.DisplayName,
                entry.File.RootKey,
                entry.File.RelativePath,
                entry.Media.Work.Id.Value,
                snapshot.Associations
                    .Where(x => x.Target == EnrichmentTarget.For(entry.Media.Work.Id) && x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed)
                    .Select(x => new MovieIndexIdentifier(x.Identifier.Provider.Value, x.Identifier.Namespace.Value, x.Identifier.Value))
                    .DistinctBy(x => (x.Provider, x.Namespace, x.Value))
                    .OrderBy(x => x.Provider, StringComparer.Ordinal)
                    .ThenBy(x => x.Namespace, StringComparer.Ordinal)
                    .ThenBy(x => x.Value, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        await using (var output = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(output, new MovieIndexDocument(DateTimeOffset.UtcNow, movies), JsonOptions, cancellationToken);
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private sealed record MovieIndexDocument(DateTimeOffset GeneratedAtUtc, IReadOnlyCollection<MovieIndexEntry> Movies);
    private sealed record MovieIndexEntry(string DisplayName, string RootKey, string RelativePath, Guid WorkId, IReadOnlyCollection<MovieIndexIdentifier> Identifiers);
    private sealed record MovieIndexIdentifier(string Provider, string Namespace, string Value);
}

public sealed class MovieMetadataIndexStartupService(MovieMetadataIndexDocument document, LibraryReadiness readiness, ILogger<MovieMetadataIndexStartupService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogIndexWriteFailed = LoggerMessage.Define(LogLevel.Warning, new EventId(1, "InitialMovieMetadataIndexWriteFailed"), "Could not write the initial movie metadata index document.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!readiness.IsReady && !stoppingToken.IsCancellationRequested)
            await Task.Delay(50, stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;
        try { await document.WriteAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { LogIndexWriteFailed(logger, exception); }
    }
}
