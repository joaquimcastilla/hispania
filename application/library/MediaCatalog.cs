using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;

namespace Hispania.Library.Application;

public interface IMediaCatalog : IMediaCatalogReader
{
    void Upsert(MediaCatalogEntry entry);

    void Replace(IEnumerable<MediaCatalogEntry> entries);
}

public sealed class InMemoryMediaCatalog : IMediaCatalog, ILibraryCatalogStore
{
    private Dictionary<MediaEntityId, MediaCatalogEntry> _entries = [];
    private Dictionary<ImportKey, ImportState> _imports = [];

    public IReadOnlyCollection<MediaCatalogEntry> GetAll() => _entries.Values.ToArray();

    public MediaCatalogEntry? GetById(MediaEntityId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _entries.GetValueOrDefault(id);
    }

    public void Upsert(MediaCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.Id] = entry;
    }

    public void Replace(IEnumerable<MediaCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var replacement = new Dictionary<MediaEntityId, MediaCatalogEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("A media catalog cannot contain null entries.", nameof(entries));
            }

            if (!replacement.TryAdd(entry.Id, entry))
            {
                throw new ArgumentException("A media catalog cannot contain duplicate MediaEntity IDs.", nameof(entries));
            }
        }

        _entries = replacement;
    }

    public ValueTask<IReadOnlyCollection<ImportIdentityResolution>> ResolveImportIdentitiesAsync(
        LibraryScanScope scope,
        IReadOnlyCollection<ImportIdentityRequest> importRequests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(importRequests);
        cancellationToken.ThrowIfCancellationRequested();

        if (importRequests.Any(request => request is null))
        {
            throw new ArgumentException("Import identity resolution cannot contain null requests.", nameof(importRequests));
        }

        if (importRequests.Any(request => !scope.Includes(request.ImportKey)))
        {
            throw new ArgumentException("Every import identity request must belong to the scan scope.", nameof(importRequests));
        }

        if (importRequests.Select(request => request.ImportKey).Distinct().Count() != importRequests.Count)
        {
            throw new ArgumentException("Import identity resolution cannot contain duplicate import keys.", nameof(importRequests));
        }

        IReadOnlyCollection<ImportIdentityResolution> resolutions = importRequests.Select(request =>
        {
            if (!_imports.TryGetValue(request.ImportKey, out var state))
            {
                return new ImportIdentityResolution(request.ImportKey, false, null, null, null, request.CurrentObservedSnapshot);
            }

            var reusableIdentity = state.Identity is not null && state.LastSuccessfulSnapshot == request.CurrentObservedSnapshot
                ? state.Identity
                : null;
            return new ImportIdentityResolution(request.ImportKey, true, reusableIdentity, state.Status, state.LastSuccessfulSnapshot, request.CurrentObservedSnapshot);
        }).ToArray();

        return ValueTask.FromResult(resolutions);
    }

    public ValueTask CommitCompletedScanAsync(CompletedLibraryScan completedScan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedScan);
        cancellationToken.ThrowIfCancellationRequested();

        var replacementImports = new Dictionary<ImportKey, ImportState>(_imports);
        foreach (var observed in completedScan.ObservedImports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            replacementImports[observed.ImportKey] = observed switch
            {
                MappedImport mapped => new ImportState(LibraryImportStatus.Active, mapped.Identity, mapped.ObservedSnapshot, mapped.Entry),
                UnsupportedImport unsupported => new ImportState(LibraryImportStatus.Unsupported, null, replacementImports.GetValueOrDefault(unsupported.ImportKey)?.LastSuccessfulSnapshot, null),
                ProbeFailedImport failed => CreateProbeFailedState(failed, replacementImports.GetValueOrDefault(failed.ImportKey)),
                _ => throw new ArgumentException("The completed scan contains an unsupported observed import result.", nameof(completedScan))
            };
        }

        var observedKeys = completedScan.ObservedImports.Select(result => result.ImportKey).ToHashSet();
        foreach (var pair in replacementImports.Where(pair => completedScan.Scope.Includes(pair.Key) && !observedKeys.Contains(pair.Key)).ToArray())
        {
            replacementImports[pair.Key] = pair.Value with { Status = LibraryImportStatus.Disappeared, Entry = null };
        }

        cancellationToken.ThrowIfCancellationRequested();
        _imports = replacementImports;
        _entries = replacementImports.Values
            .Where(state => state.Entry is not null && (state.Status is LibraryImportStatus.Active or LibraryImportStatus.ProbeFailed))
            .Select(state => state.Entry!)
            .ToDictionary(entry => entry.Id);
        return ValueTask.CompletedTask;
    }

    private static ImportState CreateProbeFailedState(ProbeFailedImport failed, ImportState? previous) =>
        previous is null
            ? new ImportState(LibraryImportStatus.ProbeFailed, null, null, null)
            : previous with { Status = LibraryImportStatus.ProbeFailed };

    private sealed record ImportState(
        LibraryImportStatus Status,
        ImportedPlayableMovieIdentity? Identity,
        LibrarySourceSnapshot? LastSuccessfulSnapshot,
        MediaCatalogEntry? Entry);
}
