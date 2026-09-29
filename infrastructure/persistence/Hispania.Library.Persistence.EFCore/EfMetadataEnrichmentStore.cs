using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Globalization;

namespace Hispania.Library.Persistence.EFCore;

public sealed class EfMetadataEnrichmentStore(IDbContextFactory<LibraryCatalogDbContext> contextFactory, IUniqueConstraintViolationClassifier uniqueConstraintClassifier) : IMetadataEnrichmentStore
{
    public async ValueTask<bool> WorkExistsAsync(WorkId workId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Works.AnyAsync(x => x.Id == workId.Value, cancellationToken);
    }

    public async ValueTask<MetadataEnrichmentSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(ConsistentReadIsolationLevel(db), cancellationToken);
        var lifecycles = await db.ExternalIdentifierLifecycles.AsNoTracking().OrderBy(x => x.AssociationId).ThenBy(x => x.Ordinal).ToListAsync(cancellationToken);
        var relations = await db.ExternalIdentifierRelations.AsNoTracking().ToListAsync(cancellationToken);
        var associations = (await db.ExternalIdentifierAssociations.AsNoTracking().ToListAsync(cancellationToken)).Select(a => RehydrateAssociation(a, lifecycles.Where(x => x.AssociationId == a.Id), relations.Where(x => x.AssociationId == a.Id))).ToArray();
        var assertions = (await db.MetadataAssertions.AsNoTracking().ToListAsync(cancellationToken)).Select(RehydrateAssertion).ToArray();
        var artwork = (await db.ArtworkReferences.AsNoTracking().ToListAsync(cancellationToken)).Select(RehydrateArtwork).ToArray();
        var refresh = (await db.MetadataRefreshStates.AsNoTracking().ToListAsync(cancellationToken)).Select(RehydrateRefresh).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new MetadataEnrichmentSnapshot(associations, assertions, artwork, refresh);
    }

    public async ValueTask<ExternalIdentifierAssociation?> FindConfirmedAssociationAsync(ExternalIdentifier identifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(ConsistentReadIsolationLevel(db), cancellationToken);
        var record = await db.ExternalIdentifierAssociations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ProviderCode == identifier.Provider.Value && x.NamespaceCode == identifier.Namespace.Value &&
            x.IdentifierValue == identifier.Value && x.CurrentStatusCode == "confirmed", cancellationToken);
        if (record is null) return null;
        var lifecycle = await db.ExternalIdentifierLifecycles.AsNoTracking().Where(x => x.AssociationId == record.Id).OrderBy(x => x.Ordinal).ToListAsync(cancellationToken);
        var relations = await db.ExternalIdentifierRelations.AsNoTracking().Where(x => x.AssociationId == record.Id).ToListAsync(cancellationToken);
        var association = RehydrateAssociation(record, lifecycle, relations);
        await transaction.CommitAsync(cancellationToken);
        return association;
    }

    public async ValueTask<MovieMetadataConfirmationResult> ConfirmAssociationAsync(ExternalIdentifierAssociation association, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var workId = association.Target.WorkId ?? throw new InvalidOperationException("Confirmed movie identifiers require a Work target.");
        var confirmed = await FindConfirmedAssociationAsync(association.Identifier, cancellationToken);
        if (confirmed is not null && confirmed.Target.WorkId != workId)
            return await RejectConflictAsync(association, confirmed, occurredAt, cancellationToken);

        association.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, occurredAt,
            new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, occurredAt, association.Identifier.Provider, association.Identifier)));
        try
        {
            await ApplyAsync(new MetadataEnrichmentWrite([association], [], [], null), cancellationToken);
            return new(true);
        }
        catch (DbUpdateException exception) when (uniqueConstraintClassifier.IsConfirmedExternalIdentifierConflict(exception))
        {
            var winner = await FindConfirmedAssociationAsync(association.Identifier, cancellationToken);
            if (winner is null || winner.Target.WorkId == workId) throw;
            var candidate = (await LoadAsync(cancellationToken)).Associations.Single(x => x.Id == association.Id);
            return await RejectConflictAsync(candidate, winner, occurredAt, cancellationToken);
        }
    }

    private async ValueTask<MovieMetadataConfirmationResult> RejectConflictAsync(ExternalIdentifierAssociation attempted, ExternalIdentifierAssociation existing, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var existingWorkId = existing.Target.WorkId ?? throw new InvalidOperationException("Confirmed movie identifiers require a Work target.");
        var attemptedWorkId = attempted.Target.WorkId ?? throw new InvalidOperationException("Movie candidates require a Work target.");
        var id = attempted.Identifier;
        var reason = $"external-identifier-conflict|provider={id.Provider.Value}|namespace={id.Namespace.Value}|identifier={id.Value}|existingWorkId={existingWorkId.Value:D}|existingAssociationId={existing.Id.Value:D}|attemptedWorkId={attemptedWorkId.Value:D}|attemptedAssociationId={attempted.Id.Value:D}";
        attempted.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Rejected, occurredAt,
            new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, occurredAt, id.Provider, id), reason));
        await ApplyAsync(new MetadataEnrichmentWrite([attempted], [], [], null), cancellationToken);
        return new(false, new ExternalIdentifierConflict(id, existingWorkId, existing.Id, attemptedWorkId, attempted.Id));
    }

    public async ValueTask<MetadataEnrichmentTargetSnapshot> LoadForTargetAsync(EnrichmentTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var targetKind = TargetCode(target);
        var targetId = TargetId(target);
        var assertions = (await db.MetadataAssertions.AsNoTracking()
            .Where(x => x.TargetKind == targetKind && x.TargetId == targetId)
            .ToListAsync(cancellationToken)).Select(RehydrateAssertion).ToArray();
        var artwork = (await db.ArtworkReferences.AsNoTracking()
            .Where(x => x.TargetKind == targetKind && x.TargetId == targetId)
            .ToListAsync(cancellationToken)).Select(RehydrateArtwork).ToArray();
        return new MetadataEnrichmentTargetSnapshot(target, assertions, artwork);
    }

    public async ValueTask<IReadOnlyCollection<MetadataEnrichmentTargetSnapshot>> LoadForTargetsAsync(IReadOnlyCollection<EnrichmentTarget> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var distinct = targets.Distinct().ToArray();
        if (distinct.Length == 0) return [];
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = new List<MetadataAssertionRecord>();
        var artworkRecords = new List<ArtworkReferenceRecord>();
        foreach (var group in distinct.GroupBy(TargetCode))
        {
            var ids = group.Select(TargetId).ToArray();
            records.AddRange(await db.MetadataAssertions.AsNoTracking().Where(x => x.TargetKind == group.Key && ids.Contains(x.TargetId)).ToListAsync(cancellationToken));
            artworkRecords.AddRange(await db.ArtworkReferences.AsNoTracking().Where(x => x.TargetKind == group.Key && ids.Contains(x.TargetId)).ToListAsync(cancellationToken));
        }
        return distinct.Select(target => new MetadataEnrichmentTargetSnapshot(target,
            records.Where(x => x.TargetKind == TargetCode(target) && x.TargetId == TargetId(target)).Select(RehydrateAssertion).ToArray(),
            artworkRecords.Where(x => x.TargetKind == TargetCode(target) && x.TargetId == TargetId(target)).Select(RehydrateArtwork).ToArray())).ToArray();
    }

    public async ValueTask<ArtworkReference?> GetArtworkReferenceByIdAsync(ArtworkReferenceId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.ArtworkReferences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id.Value, cancellationToken);
        return record is null ? null : RehydrateArtwork(record);
    }

    public async ValueTask<bool> ApplyAsync(MetadataEnrichmentWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = write.RefreshState is null ? null : RefreshRecord(write.RefreshState);
        if (state is not null)
        {
            var duplicate = await db.MetadataRefreshStates.AnyAsync(x => x.TargetKind == state.TargetKind && x.TargetId == state.TargetId && x.ProviderCode == state.ProviderCode && x.ExternalIdentifierKey == state.ExternalIdentifierKey && x.MappingVersion == state.MappingVersion && x.PayloadFingerprintKey == state.PayloadFingerprintKey, cancellationToken);
            if (duplicate) return false;
        }
        foreach (var association in write.Associations)
        {
            var persistedAssociation = await db.ExternalIdentifierAssociations.SingleOrDefaultAsync(x => x.Id == association.Id.Value, cancellationToken);
            if (persistedAssociation is null)
            {
                db.ExternalIdentifierAssociations.Add(AssociationRecord(association));
                foreach (var entry in association.LifecycleEntries.Select((entry, ordinal) => LifecycleRecord(association.Id.Value, ordinal, entry))) db.ExternalIdentifierLifecycles.Add(entry);
                foreach (var relation in association.RelatedAssociations) db.ExternalIdentifierRelations.Add(RelationRecord(association.Id.Value, relation));
            }
            else
            {
                persistedAssociation.CurrentStatusCode = Code(association.CurrentStatus);
                db.ExternalIdentifierLifecycles.RemoveRange(await db.ExternalIdentifierLifecycles.Where(x => x.AssociationId == association.Id.Value).ToListAsync(cancellationToken));
                db.ExternalIdentifierRelations.RemoveRange(await db.ExternalIdentifierRelations.Where(x => x.AssociationId == association.Id.Value).ToListAsync(cancellationToken));
                foreach (var entry in association.LifecycleEntries.Select((entry, ordinal) => LifecycleRecord(association.Id.Value, ordinal, entry))) db.ExternalIdentifierLifecycles.Add(entry);
                foreach (var relation in association.RelatedAssociations) db.ExternalIdentifierRelations.Add(RelationRecord(association.Id.Value, relation));
            }
        }
        foreach (var assertion in write.Assertions)
        {
            var persistedAssertion = await db.MetadataAssertions.SingleOrDefaultAsync(x => x.Id == assertion.Id.Value, cancellationToken);
            if (persistedAssertion is null)
            {
                db.MetadataAssertions.Add(AssertionRecord(assertion));
            }
            else
            {
                persistedAssertion.StatusCode = Code(assertion.Status);
                persistedAssertion.SupersededByAssertionId = assertion.SupersededByAssertionId?.Value;
            }
        }
        foreach (var artwork in write.ArtworkReferences)
            if (!await db.ArtworkReferences.AnyAsync(x => x.Id == artwork.Id.Value, cancellationToken)) db.ArtworkReferences.Add(ArtworkRecord(artwork));
        if (state is not null) db.MetadataRefreshStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static ExternalIdentifierAssociation RehydrateAssociation(ExternalIdentifierAssociationRecord record, IEnumerable<ExternalIdentifierLifecycleRecord> lifecycle, IEnumerable<ExternalIdentifierRelationRecord> relations)
    {
        var entries = lifecycle.OrderBy(x => x.Ordinal).Select(Lifecycle).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Persisted external identifier association lacks lifecycle history.");
        var result = new ExternalIdentifierAssociation(new ExternalIdentifierAssociationId(record.Id), Identifier(record.ProviderCode, record.NamespaceCode, record.IdentifierValue), Target(record.TargetKind, record.TargetId), entries[0]);
        foreach (var entry in entries.Skip(1))
        {
            if (result.CurrentStatus == ExternalIdentifierAssociationStatus.Rejected && entry.Status == ExternalIdentifierAssociationStatus.Candidate)
                result.ReopenRejectedIdentifierConflict(entry);
            else result.TransitionTo(entry);
        }
        if (Code(result.CurrentStatus) != record.CurrentStatusCode) throw new InvalidOperationException("Persisted association current status conflicts with lifecycle history.");
        foreach (var relation in relations) result.AddRelatedAssociation(new ExternalIdentifierAssociationRelation(new ExternalIdentifierAssociationId(relation.RelatedAssociationId), RelationKind(relation.KindCode), Provenance(relation.EvidenceOriginCode, relation.EvidenceProviderCode, relation.EvidenceIdentifierProviderCode, relation.EvidenceIdentifierNamespaceCode, relation.EvidenceIdentifierValue, relation.EvidenceObservedUtcTicks, relation.EvidenceObservedOffsetMinutes, relation.EvidenceConfidence), relation.Reason));
        return result;
    }

    private static IsolationLevel ConsistentReadIsolationLevel(LibraryCatalogDbContext db) =>
        string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal)
            ? IsolationLevel.Serializable
            : IsolationLevel.RepeatableRead;

    private static MetadataAssertion RehydrateAssertion(MetadataAssertionRecord r) => new(new MetadataAssertionId(r.Id), Target(r.TargetKind, r.TargetId), new MetadataField(r.FieldCode), Value(r), Provenance(r.ProvenanceOriginCode, r.ProvenanceProviderCode, r.ProvenanceIdentifierProviderCode, r.ProvenanceIdentifierNamespaceCode, r.ProvenanceIdentifierValue, r.ProvenanceObservedUtcTicks, r.ProvenanceObservedOffsetMinutes, r.ProvenanceConfidence), AssertionStatus(r.StatusCode), r.SupersededByAssertionId is null ? null : new MetadataAssertionId(r.SupersededByAssertionId.Value));
    private static ArtworkReference RehydrateArtwork(ArtworkReferenceRecord r) => new(new ArtworkReferenceId(r.Id), Target(r.TargetKind, r.TargetId), new ArtworkRole(r.RoleCode), new ArtworkLocator(r.LocatorScheme, r.LocatorValue), Provenance(r.ProvenanceOriginCode, r.ProvenanceProviderCode, r.ProvenanceIdentifierProviderCode, r.ProvenanceIdentifierNamespaceCode, r.ProvenanceIdentifierValue, r.ProvenanceObservedUtcTicks, r.ProvenanceObservedOffsetMinutes, r.ProvenanceConfidence));
    private static MetadataRefreshState RehydrateRefresh(MetadataRefreshStateRecord r) => new(Target(r.TargetKind, r.TargetId), new ExternalIdentifierProvider(r.ProviderCode), r.ExternalIdentifierProviderCode is null ? null : Identifier(r.ExternalIdentifierProviderCode, r.ExternalIdentifierNamespaceCode!, r.ExternalIdentifierValue!), Timestamp(r.LastAttemptUtcTicks, r.LastAttemptOffsetMinutes), Timestamp(r.LastSuccessUtcTicks, r.LastSuccessOffsetMinutes), r.MappingVersion, r.PayloadFingerprint, r.StatusCode == "succeeded" ? MetadataRefreshStatus.Succeeded : MetadataRefreshStatus.Failed);

    private static ExternalIdentifierAssociationRecord AssociationRecord(ExternalIdentifierAssociation x) => new() { Id=x.Id.Value, ProviderCode=x.Identifier.Provider.Value, NamespaceCode=x.Identifier.Namespace.Value, IdentifierValue=x.Identifier.Value, CurrentStatusCode=Code(x.CurrentStatus), TargetKind=TargetCode(x.Target), TargetId=TargetId(x.Target) };
    private static ExternalIdentifierLifecycleRecord LifecycleRecord(Guid id, int ordinal, ExternalIdentifierAssociationLifecycleEntry x) { var p=x.Evidence; var time=SqliteScalarConversions.ToDateTimeOffset(p.ObservedAt); return new() { Id=Guid.CreateVersion7(), AssociationId=id, Ordinal=ordinal, StatusCode=Code(x.Status), OccurredUtcTicks=SqliteScalarConversions.ToDateTimeOffset(x.OccurredAt).UtcTicks, OccurredOffsetMinutes=SqliteScalarConversions.ToDateTimeOffset(x.OccurredAt).OffsetMinutes, Reason=x.Reason, EvidenceOriginCode=Code(p.Origin), EvidenceProviderCode=p.Provider?.Value, EvidenceIdentifierProviderCode=p.RelatedExternalIdentifier?.Provider.Value, EvidenceIdentifierNamespaceCode=p.RelatedExternalIdentifier?.Namespace.Value, EvidenceIdentifierValue=p.RelatedExternalIdentifier?.Value, EvidenceObservedUtcTicks=time.UtcTicks, EvidenceObservedOffsetMinutes=time.OffsetMinutes, EvidenceConfidence=p.Confidence is null?null:SqliteScalarConversions.ToDecimal(p.Confidence.Value) }; }
    private static ExternalIdentifierRelationRecord RelationRecord(Guid id, ExternalIdentifierAssociationRelation x) { var p=x.Evidence; var time=SqliteScalarConversions.ToDateTimeOffset(p.ObservedAt); return new() { AssociationId=id, RelatedAssociationId=x.RelatedAssociationId.Value, KindCode=Code(x.Kind), Reason=x.Reason, EvidenceOriginCode=Code(p.Origin), EvidenceProviderCode=p.Provider?.Value, EvidenceIdentifierProviderCode=p.RelatedExternalIdentifier?.Provider.Value, EvidenceIdentifierNamespaceCode=p.RelatedExternalIdentifier?.Namespace.Value, EvidenceIdentifierValue=p.RelatedExternalIdentifier?.Value, EvidenceObservedUtcTicks=time.UtcTicks, EvidenceObservedOffsetMinutes=time.OffsetMinutes, EvidenceConfidence=p.Confidence is null?null:SqliteScalarConversions.ToDecimal(p.Confidence.Value) }; }
    private static MetadataAssertionRecord AssertionRecord(MetadataAssertion x) { var p=x.Provenance; var t=SqliteScalarConversions.ToDateTimeOffset(p.ObservedAt); var r=new MetadataAssertionRecord { Id=x.Id.Value,TargetKind=TargetCode(x.Target),TargetId=TargetId(x.Target),FieldCode=x.Field.Code,ValueKind=Code(x.Value.Kind),TextValue=x.Value.TextValue,IntegerValue=x.Value.IntegerValue,DecimalValue=x.Value.DecimalValue is null?null:SqliteScalarConversions.ToDecimal(x.Value.DecimalValue.Value),BooleanValue=x.Value.BooleanValue,DateValue=x.Value.DateValue?.ToString("O", CultureInfo.InvariantCulture),UtcTicks=x.Value.DateTimeOffsetValue is null?null:SqliteScalarConversions.ToDateTimeOffset(x.Value.DateTimeOffsetValue.Value).UtcTicks,OffsetMinutes=x.Value.DateTimeOffsetValue is null?null:SqliteScalarConversions.ToDateTimeOffset(x.Value.DateTimeOffsetValue.Value).OffsetMinutes,StatusCode=Code(x.Status),SupersededByAssertionId=x.SupersededByAssertionId?.Value,ProvenanceOriginCode=Code(p.Origin),ProvenanceProviderCode=p.Provider?.Value,ProvenanceIdentifierProviderCode=p.RelatedExternalIdentifier?.Provider.Value,ProvenanceIdentifierNamespaceCode=p.RelatedExternalIdentifier?.Namespace.Value,ProvenanceIdentifierValue=p.RelatedExternalIdentifier?.Value,ProvenanceObservedUtcTicks=t.UtcTicks,ProvenanceObservedOffsetMinutes=t.OffsetMinutes,ProvenanceConfidence=p.Confidence is null?null:SqliteScalarConversions.ToDecimal(p.Confidence.Value) }; return r; }
    private static ArtworkReferenceRecord ArtworkRecord(ArtworkReference x) { var p=x.Provenance; var t=SqliteScalarConversions.ToDateTimeOffset(p.ObservedAt); return new() { Id=x.Id.Value,TargetKind=TargetCode(x.Target),TargetId=TargetId(x.Target),RoleCode=x.Role.Code,LocatorScheme=x.Locator.Scheme,LocatorValue=x.Locator.OpaqueValue,ProvenanceOriginCode=Code(p.Origin),ProvenanceProviderCode=p.Provider?.Value,ProvenanceIdentifierProviderCode=p.RelatedExternalIdentifier?.Provider.Value,ProvenanceIdentifierNamespaceCode=p.RelatedExternalIdentifier?.Namespace.Value,ProvenanceIdentifierValue=p.RelatedExternalIdentifier?.Value,ProvenanceObservedUtcTicks=t.UtcTicks,ProvenanceObservedOffsetMinutes=t.OffsetMinutes,ProvenanceConfidence=p.Confidence is null?null:SqliteScalarConversions.ToDecimal(p.Confidence.Value) }; }
    private static MetadataRefreshStateRecord RefreshRecord(MetadataRefreshState x) { var a=x.LastAttemptAt is null?default:SqliteScalarConversions.ToDateTimeOffset(x.LastAttemptAt.Value); var s=x.LastSuccessAt is null?default:SqliteScalarConversions.ToDateTimeOffset(x.LastSuccessAt.Value); var key=x.ExternalIdentifier is null?"":$"{x.ExternalIdentifier.Provider.Value}/{x.ExternalIdentifier.Namespace.Value}/{x.ExternalIdentifier.Value}"; return new() { Id=Guid.CreateVersion7(),TargetKind=TargetCode(x.Target),TargetId=TargetId(x.Target),ProviderCode=x.Provider.Value,ExternalIdentifierKey=key,ExternalIdentifierProviderCode=x.ExternalIdentifier?.Provider.Value,ExternalIdentifierNamespaceCode=x.ExternalIdentifier?.Namespace.Value,ExternalIdentifierValue=x.ExternalIdentifier?.Value,LastAttemptUtcTicks=x.LastAttemptAt is null?null:a.UtcTicks,LastAttemptOffsetMinutes=x.LastAttemptAt is null?null:a.OffsetMinutes,LastSuccessUtcTicks=x.LastSuccessAt is null?null:s.UtcTicks,LastSuccessOffsetMinutes=x.LastSuccessAt is null?null:s.OffsetMinutes,MappingVersion=x.MappingVersion,PayloadFingerprintKey=x.PayloadFingerprint??"",PayloadFingerprint=x.PayloadFingerprint,StatusCode=x.Status==MetadataRefreshStatus.Succeeded?"succeeded":"failed" }; }
    private static ExternalIdentifierAssociationLifecycleEntry Lifecycle(ExternalIdentifierLifecycleRecord x) => new(Status(x.StatusCode), SqliteScalarConversions.FromDateTimeOffset(x.OccurredUtcTicks,x.OccurredOffsetMinutes), Provenance(x.EvidenceOriginCode,x.EvidenceProviderCode,x.EvidenceIdentifierProviderCode,x.EvidenceIdentifierNamespaceCode,x.EvidenceIdentifierValue,x.EvidenceObservedUtcTicks,x.EvidenceObservedOffsetMinutes,x.EvidenceConfidence),x.Reason);
    private static MetadataProvenance Provenance(string origin,string? provider,string? idProvider,string? idNamespace,string? idValue,long ticks,short offset,string? confidence) => new(origin=="external-provider"?MetadataProvenanceOrigin.ExternalProvider:origin=="local-process"?MetadataProvenanceOrigin.LocalProcess:MetadataProvenanceOrigin.Manual,SqliteScalarConversions.FromDateTimeOffset(ticks,offset),provider is null?null:new ExternalIdentifierProvider(provider),idProvider is null?null:Identifier(idProvider,idNamespace!,idValue!),confidence is null?null:SqliteScalarConversions.FromDecimal(confidence));
    private static MetadataValue Value(MetadataAssertionRecord r) => r.ValueKind switch { "text"=>MetadataValue.Text(r.TextValue!),"integer"=>MetadataValue.Integer(r.IntegerValue!.Value),"decimal"=>MetadataValue.Decimal(SqliteScalarConversions.FromDecimal(r.DecimalValue!)),"boolean"=>MetadataValue.Boolean(r.BooleanValue!.Value),"date"=>MetadataValue.Date(DateOnly.Parse(r.DateValue!, CultureInfo.InvariantCulture)),"date-time-offset"=>MetadataValue.DateTimeOffset(SqliteScalarConversions.FromDateTimeOffset(r.UtcTicks!.Value,r.OffsetMinutes!.Value)),_=>throw new InvalidOperationException("Unknown metadata value kind.") };
    private static ExternalIdentifier Identifier(string p,string n,string v)=>new(new ExternalIdentifierProvider(p),new ExternalIdentifierNamespace(n),v);
    private static EnrichmentTarget Target(string k,Guid id)=>k switch { "work"=>EnrichmentTarget.For(new WorkId(id)),"media-entity"=>EnrichmentTarget.For(new MediaEntityId(id)),"release"=>EnrichmentTarget.For(new ReleaseId(id)),"release-group"=>EnrichmentTarget.For(new ReleaseGroupId(id)),"collection"=>EnrichmentTarget.For(new CollectionId(id)),"creator"=>EnrichmentTarget.For(new CreatorId(id)),"artwork-reference"=>EnrichmentTarget.For(new ArtworkReferenceId(id)),_=>throw new InvalidOperationException("Unknown enrichment target.")};
    private static string TargetCode(EnrichmentTarget x)=>x.Kind switch { EnrichmentTargetKind.Work=>"work",EnrichmentTargetKind.MediaEntity=>"media-entity",EnrichmentTargetKind.Release=>"release",EnrichmentTargetKind.ReleaseGroup=>"release-group",EnrichmentTargetKind.Collection=>"collection",EnrichmentTargetKind.Creator=>"creator",_=>"artwork-reference"};
    private static Guid TargetId(EnrichmentTarget x)=>(x.WorkId?.Value??x.MediaEntityId?.Value??x.ReleaseId?.Value??x.ReleaseGroupId?.Value??x.CollectionId?.Value??x.CreatorId?.Value??x.ArtworkReferenceId?.Value)!.Value;
    private static ExternalIdentifierAssociationStatus Status(string x)=>Enum.Parse<ExternalIdentifierAssociationStatus>(x,true);
    private static ExternalIdentifierAssociationRelationKind RelationKind(string x)=>Enum.Parse<ExternalIdentifierAssociationRelationKind>(x,true);
    private static MetadataAssertionStatus AssertionStatus(string x)=>Enum.Parse<MetadataAssertionStatus>(x,true);
    private static DateTimeOffset? Timestamp(long? ticks,short? offset)=>ticks is null?null:SqliteScalarConversions.FromDateTimeOffset(ticks.Value,offset??0);
    private static string Code<T>(T value) where T:struct,Enum => string.Concat(value.ToString().Select((c,i)=>i>0&&char.IsUpper(c)?"-"+char.ToLowerInvariant(c):char.ToLowerInvariant(c).ToString()));
}
