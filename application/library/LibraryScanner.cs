using Hispania.Library.Contracts;

namespace Hispania.Library.Application;

public sealed record LibraryScanResult(int Discovered, int Imported, int Skipped, int Failed);

public sealed class LibraryScanner
{
    private readonly IMediaDiscovery _discovery;
    private readonly IMediaProbe _probe;
    private readonly IProbedMediaMapper _mapper;
    private readonly ILibraryCatalogStore _catalogStore;
    private readonly IImportedPlayableMovieIdentityFactory _identityFactory;
    private readonly LibraryScanScope _scope;

    public LibraryScanner(
        IMediaDiscovery discovery,
        IMediaProbe probe,
        IProbedMediaMapper mapper,
        ILibraryCatalogStore catalogStore,
        IImportedPlayableMovieIdentityFactory identityFactory,
        LibraryScanScope scope)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    public async Task<LibraryScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new List<DiscoveredMediaCandidate>();
        var discovered = 0;

        await foreach (var candidate in _discovery.DiscoverAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var importKey = ToImportKey(candidate);
            if (!_scope.Includes(importKey))
            {
                throw new InvalidOperationException("The media discovery returned a candidate outside the configured scan scope.");
            }

            discovered++;
            candidates.Add(candidate);
        }

        var identityRequests = candidates.Select(candidate => new ImportIdentityRequest(
            ToImportKey(candidate),
            new LibrarySourceSnapshot(candidate.ByteLength, candidate.LastModifiedUtc))).ToArray();
        var resolutions = await _catalogStore.ResolveImportIdentitiesAsync(_scope, identityRequests, cancellationToken);
        var resolutionsByKey = resolutions.ToDictionary(resolution => resolution.ImportKey);
        if (resolutionsByKey.Count != identityRequests.Length || identityRequests.Any(request => !resolutionsByKey.ContainsKey(request.ImportKey)))
        {
            throw new InvalidOperationException("The catalog store did not resolve every observed import identity.");
        }

        var observedImports = new List<ObservedImportResult>();
        var skipped = 0;
        var failed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var importKey = ToImportKey(candidate);
            var snapshot = new LibrarySourceSnapshot(candidate.ByteLength, candidate.LastModifiedUtc);
            var resolution = resolutionsByKey[importKey];
            var outcome = await _probe.ProbeAsync(candidate, cancellationToken);

            switch (outcome)
            {
                case ProbeSucceeded succeeded:
                    var identity = resolution.ReusableIdentity ?? _identityFactory.Create();
                    var mapping = _mapper.Map(candidate, succeeded, identity);
                    if (mapping is MediaMappingSucceeded imported)
                    {
                        observedImports.Add(new MappedImport(importKey, snapshot, identity, imported.Entry));
                    }
                    else if (((MediaMappingRejected)mapping).Kind == MediaMappingFailureKind.UnsupportedMedia)
                    {
                        skipped++;
                        observedImports.Add(new UnsupportedImport(importKey, snapshot, ((MediaMappingRejected)mapping).Detail));
                    }
                    else
                    {
                        failed++;
                        observedImports.Add(new ProbeFailedImport(importKey, snapshot, ((MediaMappingRejected)mapping).Detail));
                    }
                    break;
                case ProbeFailed { Failure.Kind: ProbeFailureKind.UnsupportedFormat }:
                    skipped++;
                    observedImports.Add(new UnsupportedImport(importKey, snapshot, "The media probe reported an unsupported format."));
                    break;
                case ProbeFailed probeFailed:
                    failed++;
                    observedImports.Add(new ProbeFailedImport(importKey, snapshot, probeFailed.Failure.Detail ?? probeFailed.Failure.Kind.ToString()));
                    break;
                default:
                    failed++;
                    observedImports.Add(new ProbeFailedImport(importKey, snapshot, "The media probe returned an unknown outcome."));
                    break;
            }
        }

        await _catalogStore.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, _scope, observedImports), cancellationToken);
        return new LibraryScanResult(discovered, observedImports.OfType<MappedImport>().Count(), skipped, failed);
    }

    private static ImportKey ToImportKey(DiscoveredMediaCandidate candidate) =>
        new(candidate.File.RootKey, candidate.File.RelativePath);
}
