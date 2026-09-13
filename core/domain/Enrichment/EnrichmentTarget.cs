using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Enrichment;

public sealed record EnrichmentTarget
{
    private EnrichmentTarget(
        EnrichmentTargetKind kind,
        WorkId? workId,
        MediaEntityId? mediaEntityId,
        ReleaseId? releaseId,
        ReleaseGroupId? releaseGroupId,
        CollectionId? collectionId,
        CreatorId? creatorId,
        ArtworkReferenceId? artworkReferenceId)
    {
        Kind = kind;
        WorkId = workId;
        MediaEntityId = mediaEntityId;
        ReleaseId = releaseId;
        ReleaseGroupId = releaseGroupId;
        CollectionId = collectionId;
        CreatorId = creatorId;
        ArtworkReferenceId = artworkReferenceId;
    }

    public EnrichmentTargetKind Kind { get; }

    public WorkId? WorkId { get; }

    public MediaEntityId? MediaEntityId { get; }

    public ReleaseId? ReleaseId { get; }

    public ReleaseGroupId? ReleaseGroupId { get; }

    public CollectionId? CollectionId { get; }

    public CreatorId? CreatorId { get; }

    public ArtworkReferenceId? ArtworkReferenceId { get; }

    public static EnrichmentTarget For(WorkId id) =>
        new(EnrichmentTargetKind.Work, id ?? throw Required("work"), null, null, null, null, null, null);

    public static EnrichmentTarget For(MediaEntityId id) =>
        new(EnrichmentTargetKind.MediaEntity, null, id ?? throw Required("media-entity"), null, null, null, null, null);

    public static EnrichmentTarget For(ReleaseId id) =>
        new(EnrichmentTargetKind.Release, null, null, id ?? throw Required("release"), null, null, null, null);

    public static EnrichmentTarget For(ReleaseGroupId id) =>
        new(EnrichmentTargetKind.ReleaseGroup, null, null, null, id ?? throw Required("release-group"), null, null, null);

    public static EnrichmentTarget For(CollectionId id) =>
        new(EnrichmentTargetKind.Collection, null, null, null, null, id ?? throw Required("collection"), null, null);

    public static EnrichmentTarget For(CreatorId id) =>
        new(EnrichmentTargetKind.Creator, null, null, null, null, null, id ?? throw Required("creator"), null);

    public static EnrichmentTarget For(ArtworkReferenceId id) =>
        new(EnrichmentTargetKind.ArtworkReference, null, null, null, null, null, null, id ?? throw Required("artwork-reference"));

    private static DomainValidationException Required(string targetKind) =>
        new("enrichment-target." + targetKind + "-id.required", "An " + targetKind + " enrichment target ID is required.");
}
