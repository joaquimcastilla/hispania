using Hispania.Media.Domain.Identity;

namespace Hispania.Library.Contracts;

public enum LibraryImportStatus
{
    Active,
    Disappeared,
    Unsupported,
    ProbeFailed
}

public sealed record ImportedAssetIdentity
{
    public ImportedAssetIdentity(AssetId assetId, TechnicalRepresentationId technicalRepresentationId, AssetCoverageId assetCoverageId)
    {
        AssetId = assetId ?? throw new ArgumentNullException(nameof(assetId));
        TechnicalRepresentationId = technicalRepresentationId ?? throw new ArgumentNullException(nameof(technicalRepresentationId));
        AssetCoverageId = assetCoverageId ?? throw new ArgumentNullException(nameof(assetCoverageId));
    }

    public AssetId AssetId { get; }

    public TechnicalRepresentationId TechnicalRepresentationId { get; }

    public AssetCoverageId AssetCoverageId { get; }
}

/// <summary>
/// Identity bundle for the currently supported playable-movie importer slice only.
/// </summary>
public sealed record ImportedPlayableMovieIdentity
{
    public ImportedPlayableMovieIdentity(
        WorkId workId,
        MediaEntityId mediaEntityId,
        TimelineId timelineId,
        MediaSourceId mediaSourceId,
        ImportedAssetIdentity mainVideo,
        ImportedAssetIdentity mainAudio)
    {
        WorkId = workId ?? throw new ArgumentNullException(nameof(workId));
        MediaEntityId = mediaEntityId ?? throw new ArgumentNullException(nameof(mediaEntityId));
        TimelineId = timelineId ?? throw new ArgumentNullException(nameof(timelineId));
        MediaSourceId = mediaSourceId ?? throw new ArgumentNullException(nameof(mediaSourceId));
        MainVideo = mainVideo ?? throw new ArgumentNullException(nameof(mainVideo));
        MainAudio = mainAudio ?? throw new ArgumentNullException(nameof(mainAudio));
    }

    public WorkId WorkId { get; }

    public MediaEntityId MediaEntityId { get; }

    public TimelineId TimelineId { get; }

    public MediaSourceId MediaSourceId { get; }

    public ImportedAssetIdentity MainVideo { get; }

    public ImportedAssetIdentity MainAudio { get; }
}

public sealed record LibraryScanScope
{
    private readonly HashSet<string> _rootKeys;

    public LibraryScanScope(IEnumerable<string> rootKeys)
    {
        ArgumentNullException.ThrowIfNull(rootKeys);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rootKey in rootKeys)
        {
            LibraryPathValidation.ValidateRootKey(rootKey, nameof(rootKeys));
            if (!keys.Add(rootKey))
            {
                throw new ArgumentException("A library scan scope cannot contain duplicate root keys.", nameof(rootKeys));
            }
        }

        if (keys.Count == 0)
        {
            throw new ArgumentException("A library scan scope must include at least one root key.", nameof(rootKeys));
        }

        _rootKeys = keys;
    }

    public IReadOnlyCollection<string> RootKeys => _rootKeys.ToArray();

    public bool Includes(ImportKey importKey)
    {
        ArgumentNullException.ThrowIfNull(importKey);
        return _rootKeys.Contains(importKey.RootKey);
    }
}

public sealed record ImportIdentityRequest
{
    public ImportIdentityRequest(ImportKey importKey, LibrarySourceSnapshot currentObservedSnapshot)
    {
        ImportKey = importKey ?? throw new ArgumentNullException(nameof(importKey));
        CurrentObservedSnapshot = currentObservedSnapshot ?? throw new ArgumentNullException(nameof(currentObservedSnapshot));
    }

    public ImportKey ImportKey { get; }

    public LibrarySourceSnapshot CurrentObservedSnapshot { get; }
}

public sealed record ImportIdentityResolution
{
    public ImportIdentityResolution(
        ImportKey importKey,
        bool isKnown,
        ImportedPlayableMovieIdentity? reusableIdentity,
        LibraryImportStatus? lastKnownStatus,
        LibrarySourceSnapshot? lastSuccessfulSnapshot,
        LibrarySourceSnapshot currentObservedSnapshot)
    {
        ImportKey = importKey ?? throw new ArgumentNullException(nameof(importKey));
        CurrentObservedSnapshot = currentObservedSnapshot ?? throw new ArgumentNullException(nameof(currentObservedSnapshot));

        if (!isKnown && (reusableIdentity is not null || lastKnownStatus is not null || lastSuccessfulSnapshot is not null))
        {
            throw new ArgumentException("A new import cannot expose persisted import state.", nameof(isKnown));
        }

        if (lastKnownStatus is not null && !Enum.IsDefined(lastKnownStatus.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(lastKnownStatus), "A persisted import status must be defined.");
        }

        IsKnown = isKnown;
        ReusableIdentity = reusableIdentity;
        LastKnownStatus = lastKnownStatus;
        LastSuccessfulSnapshot = lastSuccessfulSnapshot;
    }

    public ImportKey ImportKey { get; }

    public bool IsKnown { get; }

    public ImportedPlayableMovieIdentity? ReusableIdentity { get; }

    public LibraryImportStatus? LastKnownStatus { get; }

    public LibrarySourceSnapshot? LastSuccessfulSnapshot { get; }

    public LibrarySourceSnapshot CurrentObservedSnapshot { get; }
}

public abstract record ObservedImportResult
{
    protected ObservedImportResult(ImportKey importKey, LibrarySourceSnapshot observedSnapshot)
    {
        ImportKey = importKey ?? throw new ArgumentNullException(nameof(importKey));
        ObservedSnapshot = observedSnapshot ?? throw new ArgumentNullException(nameof(observedSnapshot));
    }

    public ImportKey ImportKey { get; }

    public LibrarySourceSnapshot ObservedSnapshot { get; }
}

public sealed record MappedImport : ObservedImportResult
{
    public MappedImport(ImportKey importKey, LibrarySourceSnapshot observedSnapshot, ImportedPlayableMovieIdentity identity, MediaCatalogEntry entry)
        : base(importKey, observedSnapshot)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        if (!string.Equals(entry.File.RootKey, importKey.RootKey, StringComparison.Ordinal) || !string.Equals(entry.File.RelativePath, importKey.NormalizedRelativePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("A mapped catalog entry must belong to its import key.", nameof(entry));
        }
    }

    public ImportedPlayableMovieIdentity Identity { get; }

    public MediaCatalogEntry Entry { get; }
}

public sealed record UnsupportedImport : ObservedImportResult
{
    public UnsupportedImport(ImportKey importKey, LibrarySourceSnapshot observedSnapshot, string detail)
        : base(importKey, observedSnapshot) => Detail = ImportContractValidation.RequireDetail(detail);

    public string Detail { get; }
}

public sealed record ProbeFailedImport : ObservedImportResult
{
    public ProbeFailedImport(ImportKey importKey, LibrarySourceSnapshot observedSnapshot, string detail)
        : base(importKey, observedSnapshot) => Detail = ImportContractValidation.RequireDetail(detail);

    public string Detail { get; }
}

public sealed record CompletedLibraryScan
{
    public CompletedLibraryScan(DateTimeOffset completedAt, LibraryScanScope scope, IEnumerable<ObservedImportResult> observedImports)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(observedImports);
        var results = observedImports.ToArray();
        if (results.Any(result => result is null))
        {
            throw new ArgumentException("A completed scan cannot contain null observed imports.", nameof(observedImports));
        }

        if (results.Any(result => !scope.Includes(result.ImportKey)))
        {
            throw new ArgumentException("Every observed import must belong to the completed scan scope.", nameof(observedImports));
        }

        if (results.Select(result => result.ImportKey).Distinct().Count() != results.Length)
        {
            throw new ArgumentException("A completed scan cannot observe the same import key more than once.", nameof(observedImports));
        }

        CompletedAt = completedAt;
        Scope = scope;
        ObservedImports = results;
    }

    public DateTimeOffset CompletedAt { get; }

    public LibraryScanScope Scope { get; }

    public IReadOnlyCollection<ObservedImportResult> ObservedImports { get; }
}

public interface ILibraryCatalogStore
{
    ValueTask<IReadOnlyCollection<ImportIdentityResolution>> ResolveImportIdentitiesAsync(
        LibraryScanScope scope,
        IReadOnlyCollection<ImportIdentityRequest> importRequests,
        CancellationToken cancellationToken = default);

    ValueTask CommitCompletedScanAsync(CompletedLibraryScan completedScan, CancellationToken cancellationToken = default);
}

file static class ImportContractValidation
{
    public static string RequireDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("An observed import result requires a detail.", nameof(detail));
        }

        return detail;
    }
}
