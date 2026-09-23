using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;

namespace Hispania.Metadata.Application;

public sealed class MetadataEditingService(IMetadataEnrichmentStore store, IMetadataSettingsStore settingsStore, IRichMetadataStore? richStore = null) : IMetadataEditingService
{
    public async ValueTask<MetadataEditorSnapshot> GetAsync(WorkId workId, CancellationToken cancellationToken = default)
    {
        var target = EnrichmentTarget.For(workId); var all = await store.LoadForTargetAsync(target, cancellationToken); var locks = await settingsStore.GetLocksAsync(workId, cancellationToken); var grouped = all.Assertions.Where(x => x.Status == MetadataAssertionStatus.Active).GroupBy(x => x.Field.Code).Select(group => new MetadataEditorField(group.Key, group.OrderByDescending(x => x.Provenance.Origin).First().Value, group.FirstOrDefault(x => x.Provenance.Origin == MetadataProvenanceOrigin.Manual)?.Value, group.Where(x => x.Provenance.Origin == MetadataProvenanceOrigin.ExternalProvider).Select(x => x.Value).ToArray(), locks.Any(x => x.FieldCode == group.Key && x.IsLocked), group.Select(x => new MetadataEditorSource(x.Provenance.Origin, x.Provenance.Provider?.Value, x.Provenance.ObservedAt, x.Value)).ToArray())).ToArray();
        if (richStore is null) return new(workId, await settingsStore.GetSettingsAsync(workId, cancellationToken), locks, grouped);
        var rich = await richStore.LoadAsync(target, cancellationToken);
        var collections = new[]
        {
            Collection("genres", RichMetadataCollectionType.Genres, rich, locks),
            Collection("tags", RichMetadataCollectionType.Tags, rich, locks),
            Collection("studios", RichMetadataCollectionType.Studios, rich, locks)
        };
        var personIds = rich.People.Where(x => x.InternalPersonId.HasValue).Select(x => x.InternalPersonId!.Value).Distinct().ToArray();
        var credits = (await Task.WhenAll(personIds.Select(x => richStore.GetCreditsForPersonAsync(x, cancellationToken).AsTask()))).SelectMany(x => x).Where(x => x.WorkId == workId).Select(x => new MetadataEditorCredit(x.PersonId, x.WorkId, x.CreditType, x.Character, x.Job, x.Ordinal, x.Origin, x.ProviderCode, x.ObservedAt)).ToArray();
        return new(workId, await settingsStore.GetSettingsAsync(workId, cancellationToken), locks, grouped, collections, rich.People, credits);
    }
    private static MetadataEditorCollectionProjection Collection(string code, RichMetadataCollectionType type, RichMetadataSnapshot rich, IReadOnlyCollection<MetadataFieldLock> locks)
    {
        var manual = rich.Sources.Where(x => x.Origin == MetadataProvenanceOrigin.Manual).OrderByDescending(x => x.ObservedAt).FirstOrDefault();
        var effective = EffectiveRichMetadataResolver.Resolve<string>(rich.Sources, type);
        var manualValues = manual is null ? [] : type switch { RichMetadataCollectionType.Genres => manual.Genres, RichMetadataCollectionType.Tags => manual.Tags, _ => manual.Studios.Select(x => x.Name).ToArray() };
        var lockCodes = code switch
        {
            "genres" => new[] { "genre", "genres" },
            "tags" => new[] { "tag", "tags" },
            "studios" => new[] { "studio", "studios" },
            _ => new[] { code }
        };
        return new(code, effective, manualValues, rich.Sources, locks.Any(x => x.IsLocked && lockCodes.Contains(x.FieldCode, StringComparer.OrdinalIgnoreCase)));
    }
    public async ValueTask UpdateSettingsAsync(MetadataSettings settings, CancellationToken cancellationToken = default) => await settingsStore.SetSettingsAsync(settings, cancellationToken);
    public ValueTask SetFieldLockAsync(MetadataFieldLock fieldLock, CancellationToken cancellationToken = default) => settingsStore.SetLockAsync(fieldLock, cancellationToken);
    public async ValueTask SetManualScalarAsync(WorkId workId, string fieldCode, MetadataValue value, DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    { var target = EnrichmentTarget.For(workId); var snapshot = await store.LoadForTargetAsync(target, cancellationToken); var current = snapshot.Assertions.FirstOrDefault(x => x.Target == target && x.Field.Code == fieldCode && x.Provenance.Origin == MetadataProvenanceOrigin.Manual && x.Status == MetadataAssertionStatus.Active); var assertion = new MetadataAssertion(current?.Id ?? MetadataAssertionId.New(), target, new MetadataField(fieldCode), value, new MetadataProvenance(MetadataProvenanceOrigin.Manual, observedAt), MetadataAssertionStatus.Active); await store.ApplyAsync(new MetadataEnrichmentWrite([], [assertion], [], null), cancellationToken); }
    public async ValueTask ClearManualScalarAsync(WorkId workId, string fieldCode, CancellationToken cancellationToken = default)
    { var target = EnrichmentTarget.For(workId); var snapshot = await store.LoadForTargetAsync(target, cancellationToken); var manual = snapshot.Assertions.FirstOrDefault(x => x.Field.Code == fieldCode && x.Provenance.Origin == MetadataProvenanceOrigin.Manual && x.Status == MetadataAssertionStatus.Active); if (manual is null) return; var cleared = new MetadataAssertion(manual.Id, manual.Target, manual.Field, manual.Value, manual.Provenance, MetadataAssertionStatus.Retracted); await store.ApplyAsync(new MetadataEnrichmentWrite([], [cleared], [], null), cancellationToken); }
    public ValueTask SetManualGenresAsync(WorkId workId, IReadOnlyCollection<string> genres, DateTimeOffset observedAt, CancellationToken cancellationToken = default) => SetCollection(workId, RichMetadataCollectionType.Genres, genres, observedAt, cancellationToken);
    public ValueTask ClearManualGenresAsync(WorkId workId, CancellationToken cancellationToken = default) => ClearCollection(workId, RichMetadataCollectionType.Genres, cancellationToken);
    public ValueTask SetManualTagsAsync(WorkId workId, IReadOnlyCollection<string> tags, DateTimeOffset observedAt, CancellationToken cancellationToken = default) => SetCollection(workId, RichMetadataCollectionType.Tags, tags, observedAt, cancellationToken);
    public ValueTask ClearManualTagsAsync(WorkId workId, CancellationToken cancellationToken = default) => ClearCollection(workId, RichMetadataCollectionType.Tags, cancellationToken);
    public ValueTask SetManualStudiosAsync(WorkId workId, IReadOnlyCollection<string> studios, DateTimeOffset observedAt, CancellationToken cancellationToken = default) => SetCollection(workId, RichMetadataCollectionType.Studios, studios, observedAt, cancellationToken);
    public ValueTask ClearManualStudiosAsync(WorkId workId, CancellationToken cancellationToken = default) => ClearCollection(workId, RichMetadataCollectionType.Studios, cancellationToken);
    private ValueTask SetCollection(WorkId workId, RichMetadataCollectionType collectionType, IReadOnlyCollection<string> values, DateTimeOffset observedAt, CancellationToken cancellationToken) => richStore is null ? throw new InvalidOperationException("Rich metadata editing is unavailable.") : richStore.SetManualCollectionAsync(EnrichmentTarget.For(workId), collectionType, values, observedAt, cancellationToken);
    private ValueTask ClearCollection(WorkId workId, RichMetadataCollectionType collectionType, CancellationToken cancellationToken) => richStore is null ? throw new InvalidOperationException("Rich metadata editing is unavailable.") : richStore.ClearManualCollectionAsync(EnrichmentTarget.For(workId), collectionType, cancellationToken);
}
