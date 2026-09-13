using Hispania.Library.Contracts;

namespace Hispania.Library.Application;

public sealed record LibraryScanResult(int Discovered, int Imported, int Skipped, int Failed);

public sealed class LibraryScanner
{
    private readonly IMediaDiscovery _discovery;
    private readonly IMediaProbe _probe;
    private readonly IProbedMediaMapper _mapper;
    private readonly IMediaCatalog _catalog;

    public LibraryScanner(IMediaDiscovery discovery, IMediaProbe probe, IProbedMediaMapper mapper, IMediaCatalog catalog)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<LibraryScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<MediaCatalogEntry>();
        var discovered = 0;
        var skipped = 0;
        var failed = 0;

        await foreach (var candidate in _discovery.DiscoverAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            discovered++;
            var outcome = await _probe.ProbeAsync(candidate, cancellationToken);

            switch (outcome)
            {
                case ProbeSucceeded succeeded:
                    var mapping = _mapper.Map(candidate, succeeded);
                    if (mapping is MediaMappingSucceeded imported)
                    {
                        entries.Add(imported.Entry);
                    }
                    else if (((MediaMappingRejected)mapping).Kind == MediaMappingFailureKind.UnsupportedMedia)
                    {
                        skipped++;
                    }
                    else
                    {
                        failed++;
                    }
                    break;
                case ProbeFailed { Failure.Kind: ProbeFailureKind.UnsupportedFormat }:
                    skipped++;
                    break;
                case ProbeFailed:
                    failed++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        _catalog.Replace(entries);
        return new LibraryScanResult(discovered, entries.Count, skipped, failed);
    }
}
