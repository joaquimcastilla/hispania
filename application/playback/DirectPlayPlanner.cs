using Hispania.Library.Contracts;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.Playback.Contracts;

namespace Hispania.Playback.Application;

public sealed class DirectPlayPlanner : IPlaybackPlanner
{
    private readonly IMediaCatalogReader _catalog;

    public DirectPlayPlanner(IMediaCatalogReader catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId)
    {
        ArgumentNullException.ThrowIfNull(mediaEntityId);
        var entry = _catalog.GetById(mediaEntityId);
        if (entry is null)
        {
            return new PlaybackMediaNotFound(mediaEntityId);
        }

        foreach (var video in entry.Media.VideoAsset.TechnicalRepresentations)
        {
            if (!IsH264(video.Profile))
            {
                continue;
            }

            foreach (var audio in entry.Media.AudioAsset.TechnicalRepresentations)
            {
                if (!IsAac(audio.Profile))
                {
                    continue;
                }

                var sourceId = FindSharedMp4Source(video, audio, entry.Media.Source.Id);
                if (sourceId is not null)
                {
                    return new DirectPlayPlanned(new DirectPlayPlan(entry.Id, sourceId, video.Id, audio.Id));
                }
            }
        }

        return new DirectPlayUnavailable(mediaEntityId);
    }

    internal static bool IsH264(TechnicalProfile profile) =>
        profile is VideoTechnicalProfile { Codec.Code: "h264" };

    internal static bool IsAac(TechnicalProfile profile) =>
        profile is AudioTechnicalProfile { Codec.Code: "aac" };

    internal static MediaSourceId? FindSharedMp4Source(
        TechnicalRepresentation video,
        TechnicalRepresentation audio,
        MediaSourceId availableSourceId)
    {
        var videoSources = video.SourceBindings
            .Where(binding => IsMp4Exposure(binding.ContainerExposure) && binding.MediaSourceId == availableSourceId)
            .Select(binding => binding.MediaSourceId);
        return videoSources.FirstOrDefault(sourceId => audio.SourceBindings.Any(binding =>
            binding.MediaSourceId == sourceId && IsMp4Exposure(binding.ContainerExposure)));
    }

    internal static bool IsMp4Exposure(ContainerExposure? exposure) =>
        string.Equals(exposure?.ContainerFormat.Code, "mp4", StringComparison.OrdinalIgnoreCase);
}
