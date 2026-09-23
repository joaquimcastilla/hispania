using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;

namespace Hispania.Metadata.Contracts;

public sealed record MetadataSettings(WorkId WorkId, string? PreferredMetadataLanguage, string? MetadataCountryRegion, bool LockItemMetadata);
public sealed record MetadataFieldLock(WorkId WorkId, string FieldCode, bool IsLocked);
public sealed record MetadataEditorSource(MetadataProvenanceOrigin Origin, string? ProviderCode, DateTimeOffset ObservedAt, MetadataValue Value);
public sealed record MetadataEditorField(string FieldCode, MetadataValue? EffectiveValue, MetadataValue? ManualValue, IReadOnlyCollection<MetadataValue> ProviderValues, bool IsLocked, IReadOnlyCollection<MetadataEditorSource> Sources = null!);
public sealed record MetadataEditorCredit(Guid PersonId, WorkId WorkId, string CreditType, string? Character, string? Job, int? Ordinal, MetadataProvenanceOrigin Origin, string? ProviderCode, DateTimeOffset ObservedAt);
public sealed record MetadataEditorSnapshot(WorkId WorkId, MetadataSettings Settings, IReadOnlyCollection<MetadataFieldLock> Locks, IReadOnlyCollection<MetadataEditorField> Fields, IReadOnlyCollection<MetadataEditorCollectionProjection> Collections = null!, IReadOnlyCollection<NormalizedPerson> People = null!, IReadOnlyCollection<MetadataEditorCredit> Credits = null!);
public sealed record MetadataEditorCollectionProjection(string CollectionCode, IReadOnlyCollection<string> Effective, IReadOnlyCollection<string> Manual, IReadOnlyCollection<RichMetadataSourceSnapshot> Sources, bool IsLocked);
public interface IMetadataSettingsStore
{
    ValueTask<MetadataSettings> GetSettingsAsync(WorkId workId, CancellationToken cancellationToken = default);
    ValueTask SetSettingsAsync(MetadataSettings settings, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyCollection<MetadataFieldLock>> GetLocksAsync(WorkId workId, CancellationToken cancellationToken = default);
    ValueTask SetLockAsync(MetadataFieldLock fieldLock, CancellationToken cancellationToken = default);
}
public interface IMetadataEditingService
{
    ValueTask<MetadataEditorSnapshot> GetAsync(WorkId workId, CancellationToken cancellationToken = default);
    ValueTask UpdateSettingsAsync(MetadataSettings settings, CancellationToken cancellationToken = default);
    ValueTask SetFieldLockAsync(MetadataFieldLock fieldLock, CancellationToken cancellationToken = default);
    ValueTask SetManualScalarAsync(WorkId workId, string fieldCode, MetadataValue value, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
    ValueTask ClearManualScalarAsync(WorkId workId, string fieldCode, CancellationToken cancellationToken = default);
    ValueTask SetManualGenresAsync(WorkId workId, IReadOnlyCollection<string> genres, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
    ValueTask ClearManualGenresAsync(WorkId workId, CancellationToken cancellationToken = default);
    ValueTask SetManualTagsAsync(WorkId workId, IReadOnlyCollection<string> tags, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
    ValueTask ClearManualTagsAsync(WorkId workId, CancellationToken cancellationToken = default);
    ValueTask SetManualStudiosAsync(WorkId workId, IReadOnlyCollection<string> studios, DateTimeOffset observedAt, CancellationToken cancellationToken = default);
    ValueTask ClearManualStudiosAsync(WorkId workId, CancellationToken cancellationToken = default);
}
