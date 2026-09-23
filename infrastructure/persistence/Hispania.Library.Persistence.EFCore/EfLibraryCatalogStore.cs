using Hispania.Library.Contracts;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.Media.Domain.Timelines;
using Hispania.Media.Domain.Works;
using Microsoft.EntityFrameworkCore;

namespace Hispania.Library.Persistence.EFCore;

public sealed class EfLibraryCatalogStore : ILibraryCatalogStore, IMediaCatalogReader
{
    private readonly IDbContextFactory<LibraryCatalogDbContext> _contextFactory;
    private readonly Func<CancellationToken, Task>? _afterUnitOfWorkMutation;
    private readonly object _snapshotGate = new();
    private Dictionary<MediaEntityId, MediaCatalogEntry> _snapshot = [];

    public EfLibraryCatalogStore(IDbContextFactory<LibraryCatalogDbContext> contextFactory)
        : this(contextFactory, null)
    {
    }

    internal EfLibraryCatalogStore(
        IDbContextFactory<LibraryCatalogDbContext> contextFactory,
        Func<CancellationToken, Task>? afterUnitOfWorkMutation)
    {
        _contextFactory = contextFactory;
        _afterUnitOfWorkMutation = afterUnitOfWorkMutation;
    }

    public IReadOnlyCollection<MediaCatalogEntry> GetAll() { lock (_snapshotGate) return _snapshot.Values.ToArray(); }
    public MediaCatalogEntry? GetById(MediaEntityId id) { lock (_snapshotGate) return _snapshot.GetValueOrDefault(id); }

    public async ValueTask LoadSnapshotAsync(IReadOnlyCollection<string>? configuredRootKeys = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        Publish(await LoadEntriesAsync(context, cancellationToken, configuredRootKeys));
    }

    public async ValueTask<IReadOnlyCollection<ImportIdentityResolution>> ResolveImportIdentitiesAsync(LibraryScanScope scope, IReadOnlyCollection<ImportIdentityRequest> importRequests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(importRequests);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var roots = scope.RootKeys;
        var records = await context.Imports.AsNoTracking().Where(x => roots.Contains(x.RootKey)).ToListAsync(cancellationToken);
        return importRequests.Select(request =>
        {
            var record = records.SingleOrDefault(x => x.RootKey == request.ImportKey.RootKey && x.RelativePath == request.ImportKey.NormalizedRelativePath);
            if (record is null) return new ImportIdentityResolution(request.ImportKey, false, null, null, null, request.CurrentObservedSnapshot);
            var successful = Snapshot(record.SuccessfulLength, record.SuccessfulUtcTicks, record.SuccessfulOffsetMinutes);
            var identity = successful == request.CurrentObservedSnapshot ? Identity(record) : null;
            return new ImportIdentityResolution(request.ImportKey, true, identity, Status(record.StatusCode), successful, request.CurrentObservedSnapshot);
        }).ToArray();
    }

    public async ValueTask CommitCompletedScanAsync(CompletedLibraryScan completedScan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedScan);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var roots = completedScan.Scope.RootKeys;
        var records = await context.Imports.Where(x => roots.Contains(x.RootKey)).ToListAsync(cancellationToken);
        var mutated = false;
        foreach (var observed in completedScan.ObservedImports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records.SingleOrDefault(x => x.RootKey == observed.ImportKey.RootKey && x.RelativePath == observed.ImportKey.NormalizedRelativePath);
            if (record is null) { record = new ImportRecord { Id = Guid.CreateVersion7(), RootKey = observed.ImportKey.RootKey, RelativePath = observed.ImportKey.NormalizedRelativePath }; context.Imports.Add(record); records.Add(record); }
            SetObserved(record, observed.ObservedSnapshot);
            switch (observed)
            {
                case MappedImport mapped: PersistMapped(context, record, mapped); break;
                case UnsupportedImport: record.StatusCode = "unsupported"; break;
                case ProbeFailedImport: record.StatusCode = "probe-failed"; break;
            }
            if (!mutated && _afterUnitOfWorkMutation is not null)
            {
                mutated = true;
                await _afterUnitOfWorkMutation(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        var observedKeys = completedScan.ObservedImports.Select(x => x.ImportKey).ToHashSet();
        foreach (var record in records.Where(x => !observedKeys.Contains(new ImportKey(x.RootKey, x.RelativePath)))) record.StatusCode = "disappeared";
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Publish(await LoadEntriesAsync(context, cancellationToken));
    }

    private static void PersistMapped(LibraryCatalogDbContext context, ImportRecord import, MappedImport mapped)
    {
        var media = mapped.Entry.Media; var identity = mapped.Identity;
        if (context.Works.Any(x => x.Id == identity.WorkId.Value)) { import.StatusCode = "active"; SetSuccessful(import, mapped.ObservedSnapshot); return; }
        context.Works.Add(new WorkRecord { Id = media.Work.Id.Value, CategoryCode = media.Work.Category.Code });
        context.MediaEntities.Add(new MediaEntityRecord { Id = media.MediaEntity.Id.Value, WorkId = media.Work.Id.Value, CategoryCode = media.MediaEntity.Category.Code });
        context.Timelines.Add(new TimelineRecord { Id = media.Timeline.Id.Value, MediaEntityId = media.MediaEntity.Id.Value, DurationTicks = media.Timeline.Duration?.Value.Ticks });
        PersistAsset(context, media.VideoAsset); PersistAsset(context, media.AudioAsset);
        context.Sources.Add(new SourceRecord { Id = media.Source.Id.Value, Scheme = media.Source.Locator.Scheme.Value, OpaqueValue = media.Source.Locator.OpaqueValue });
        foreach (var asset in new[] { media.VideoAsset, media.AudioAsset }) foreach (var representation in asset.TechnicalRepresentations) { PersistRepresentation(context, representation); foreach (var binding in representation.SourceBindings) context.Bindings.Add(new BindingRecord { RepresentationId = representation.Id.Value, SourceId = binding.MediaSourceId.Value, ContainerNamespace = binding.ContainerExposure?.ContainerFormat.Namespace, ContainerCode = binding.ContainerExposure?.ContainerFormat.Code, StreamIndex = binding.ContainerExposure?.StreamLocator.StreamIndex }); }
        foreach (var coverage in media.Timeline.Coverages) context.Coverages.Add(new CoverageRecord { Id = coverage.Id.Value, TimelineId = coverage.TimelineId.Value, AssetId = coverage.AssetId.Value, StateCode = coverage.State == AssetCoverageState.Covered ? "covered" : "incompatible", CoverageKindCode = coverage.CoverageKind?.ToString().ToLowerInvariant(), AlignmentOffsetTicks = coverage.Alignment?.OffsetValue.Ticks });
        import.StatusCode = "active"; import.WorkId = identity.WorkId.Value; import.MediaEntityId = identity.MediaEntityId.Value; import.TimelineId = identity.TimelineId.Value; import.SourceId = identity.MediaSourceId.Value; import.VideoAssetId = identity.MainVideo.AssetId.Value; import.VideoRepresentationId = identity.MainVideo.TechnicalRepresentationId.Value; import.VideoCoverageId = identity.MainVideo.AssetCoverageId.Value; import.AudioAssetId = identity.MainAudio.AssetId.Value; import.AudioRepresentationId = identity.MainAudio.TechnicalRepresentationId.Value; import.AudioCoverageId = identity.MainAudio.AssetCoverageId.Value; SetSuccessful(import, mapped.ObservedSnapshot);
    }
    private static void PersistAsset(LibraryCatalogDbContext c, Asset a) { c.Assets.Add(new AssetRecord { Id = a.Id.Value, MediaKindCode = a.MediaKind.ToString().ToLowerInvariant() }); foreach (var role in a.Roles) c.AssetRoles.Add(new AssetRoleRecord { AssetId = a.Id.Value, RoleCode = role.Code }); }
    private static void PersistRepresentation(LibraryCatalogDbContext c, TechnicalRepresentation r) { var p = r.Profile; var record = new RepresentationRecord { Id=r.Id.Value, AssetId=r.AssetId.Value, ProfileKind=p switch { VideoTechnicalProfile=>"video", AudioTechnicalProfile=>"audio", _=>throw new NotSupportedException("Only the playable slice is writable in Persistence 0.1 Stage 2.") } }; if (p is VideoTechnicalProfile v) { record.CodecNamespace=v.Codec.Namespace;record.CodecCode=v.Codec.Code;record.Width=v.Dimensions.Width;record.Height=v.Dimensions.Height;record.FrameRateNumerator=v.FrameRate.Numerator;record.FrameRateDenominator=v.FrameRate.Denominator;record.BitDepth=v.BitDepth;record.BitRate=v.BitRate?.BitsPerSecond; } else { var a=(AudioTechnicalProfile)p;record.CodecNamespace=a.Codec.Namespace;record.CodecCode=a.Codec.Code;record.SampleRate=a.SampleRateHertz;record.ChannelCount=a.ChannelCount;record.BitDepth=a.BitDepth;record.BitRate=a.BitRate?.BitsPerSecond; } c.Representations.Add(record); }
    private static async Task<Dictionary<MediaEntityId, MediaCatalogEntry>> LoadEntriesAsync(LibraryCatalogDbContext c, CancellationToken ct, IReadOnlyCollection<string>? configuredRootKeys = null) { var importsQuery=c.Imports.AsNoTracking().Where(x=>x.StatusCode=="active"||x.StatusCode=="probe-failed"); if (configuredRootKeys is not null) importsQuery=importsQuery.Where(x=>configuredRootKeys.Contains(x.RootKey)); var imports=await importsQuery.ToListAsync(ct); var entries=new Dictionary<MediaEntityId,MediaCatalogEntry>(); foreach(var i in imports) { if (Identity(i) is not { } id) continue; var w=await c.Works.AsNoTracking().SingleAsync(x=>x.Id==id.WorkId.Value,ct); var e=await c.MediaEntities.AsNoTracking().SingleAsync(x=>x.Id==id.MediaEntityId.Value,ct); var t=await c.Timelines.AsNoTracking().SingleAsync(x=>x.Id==id.TimelineId.Value,ct); var source=await c.Sources.AsNoTracking().SingleAsync(x=>x.Id==id.MediaSourceId.Value,ct); var work=new Work(id.WorkId,WorkCategory.From(w.CategoryCode));var entity=work.AddMediaEntity(id.MediaEntityId,MediaEntityCategory.From(e.CategoryCode));var timeline=entity.AddTimeline(id.TimelineId,t.DurationTicks is null?null:new TimelineDuration(TimeSpan.FromTicks(t.DurationTicks.Value)));var src=new MediaSource(id.MediaSourceId,new SourceLocator(new SourceScheme(source.Scheme),source.OpaqueValue));var video=await RehydrateAsset(c,id.MainVideo,src,ct);var audio=await RehydrateAsset(c,id.MainAudio,src,ct); foreach(var coverage in await c.Coverages.AsNoTracking().Where(x=>x.TimelineId==id.TimelineId.Value).ToListAsync(ct)) timeline.AddCoverage(AssetCoverage.CreateCovered(new AssetCoverageId(coverage.Id),timeline.Id,new AssetId(coverage.AssetId),coverage.CoverageKindCode=="full"?CoverageKind.Full:CoverageKind.Partial,null,coverage.AlignmentOffsetTicks is 0?TemporalAlignment.Identity:TemporalAlignment.Offset(TimeSpan.FromTicks(coverage.AlignmentOffsetTicks??0)))); entries[entity.Id]=new MediaCatalogEntry(entity.Id,Path.GetFileNameWithoutExtension(i.RelativePath),new LibraryFileReference(i.RootKey,i.RelativePath),Snapshot(i.SuccessfulLength,i.SuccessfulUtcTicks,i.SuccessfulOffsetMinutes)!,new ImportedMedia(work,entity,timeline,video,audio,src)); } return entries; }
    private static async Task<Asset> RehydrateAsset(LibraryCatalogDbContext c, ImportedAssetIdentity id, MediaSource source, CancellationToken ct) { var a=await c.Assets.AsNoTracking().SingleAsync(x=>x.Id==id.AssetId.Value,ct);var roles=(await c.AssetRoles.AsNoTracking().Where(x=>x.AssetId==a.Id).ToListAsync(ct)).Select(x=>AssetRole.From(x.RoleCode));var asset=new Asset(id.AssetId,a.MediaKindCode=="video"?AssetMediaKind.Video:AssetMediaKind.Audio,roles);var r=await c.Representations.AsNoTracking().SingleAsync(x=>x.Id==id.TechnicalRepresentationId.Value,ct);TechnicalProfile profile=r.ProfileKind=="video"?new VideoTechnicalProfile(new PixelDimensions(r.Width!.Value,r.Height!.Value),new CodecIdentifier(r.CodecNamespace,r.CodecCode),new FrameRate(r.FrameRateNumerator!.Value,r.FrameRateDenominator!.Value),r.BitDepth,r.BitRate is null?null:new BitRate(r.BitRate.Value)):new AudioTechnicalProfile(new CodecIdentifier(r.CodecNamespace,r.CodecCode),r.SampleRate!.Value,r.ChannelCount!.Value,r.BitDepth,r.BitRate is null?null:new BitRate(r.BitRate.Value));var rep=asset.AddTechnicalRepresentation(id.TechnicalRepresentationId,profile);foreach(var b in await c.Bindings.AsNoTracking().Where(x=>x.RepresentationId==r.Id).ToListAsync(ct)) rep.AddSourceBinding(source,b.StreamIndex is null?null:new ContainerExposure(new ContainerFormat(b.ContainerNamespace!,b.ContainerCode!),new StreamLocator(b.StreamIndex.Value)));return asset; }
    private void Publish(Dictionary<MediaEntityId,MediaCatalogEntry> entries) { lock(_snapshotGate) _snapshot=entries; }
    private static ImportedPlayableMovieIdentity? Identity(ImportRecord x) => x.WorkId is null||x.MediaEntityId is null||x.TimelineId is null||x.SourceId is null||x.VideoAssetId is null||x.VideoRepresentationId is null||x.VideoCoverageId is null||x.AudioAssetId is null||x.AudioRepresentationId is null||x.AudioCoverageId is null?null:new ImportedPlayableMovieIdentity(new WorkId(x.WorkId.Value),new MediaEntityId(x.MediaEntityId.Value),new TimelineId(x.TimelineId.Value),new MediaSourceId(x.SourceId.Value),new ImportedAssetIdentity(new AssetId(x.VideoAssetId.Value),new TechnicalRepresentationId(x.VideoRepresentationId.Value),new AssetCoverageId(x.VideoCoverageId.Value)),new ImportedAssetIdentity(new AssetId(x.AudioAssetId.Value),new TechnicalRepresentationId(x.AudioRepresentationId.Value),new AssetCoverageId(x.AudioCoverageId.Value)));
    private static LibraryImportStatus Status(string x)=>x switch {"active"=>LibraryImportStatus.Active,"disappeared"=>LibraryImportStatus.Disappeared,"unsupported"=>LibraryImportStatus.Unsupported,"probe-failed"=>LibraryImportStatus.ProbeFailed,_=>throw new InvalidOperationException("Unknown persisted import status.")};
    private static LibrarySourceSnapshot? Snapshot(long? l,long? t,short? o)=>t is null?null:new LibrarySourceSnapshot(l,SqliteScalarConversions.FromDateTimeOffset(t.Value,o??0));
    private static void SetObserved(ImportRecord x,LibrarySourceSnapshot s){x.ObservedLength=s.ByteLength;if(s.LastModifiedUtc is { } d){var v=SqliteScalarConversions.ToDateTimeOffset(d);x.ObservedUtcTicks=v.UtcTicks;x.ObservedOffsetMinutes=v.OffsetMinutes;}else{x.ObservedUtcTicks=null;x.ObservedOffsetMinutes=null;}}
    private static void SetSuccessful(ImportRecord x,LibrarySourceSnapshot s){x.SuccessfulLength=s.ByteLength;if(s.LastModifiedUtc is { } d){var v=SqliteScalarConversions.ToDateTimeOffset(d);x.SuccessfulUtcTicks=v.UtcTicks;x.SuccessfulOffsetMinutes=v.OffsetMinutes;}else{x.SuccessfulUtcTicks=null;x.SuccessfulOffsetMinutes=null;}}
}
