using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Creators;

public sealed record ContributionTarget
{
    private ContributionTarget(
        ContributionTargetKind kind,
        WorkId? workId,
        MediaEntityId? mediaEntityId,
        ReleaseId? releaseId)
    {
        Kind = kind;
        WorkId = workId;
        MediaEntityId = mediaEntityId;
        ReleaseId = releaseId;
    }

    public ContributionTargetKind Kind { get; }

    public WorkId? WorkId { get; }

    public MediaEntityId? MediaEntityId { get; }

    public ReleaseId? ReleaseId { get; }

    public static ContributionTarget For(WorkId workId) =>
        new(ContributionTargetKind.Work, workId ?? throw Required("work"), null, null);

    public static ContributionTarget For(MediaEntityId mediaEntityId) =>
        new(ContributionTargetKind.MediaEntity, null, mediaEntityId ?? throw Required("media-entity"), null);

    public static ContributionTarget For(ReleaseId releaseId) =>
        new(ContributionTargetKind.Release, null, null, releaseId ?? throw Required("release"));

    private static DomainValidationException Required(string targetKind) =>
        new("contribution-target." + targetKind + "-id.required", "A " + targetKind + " contribution target ID is required.");
}
