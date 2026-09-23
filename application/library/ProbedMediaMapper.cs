using Hispania.Library.Contracts;
using Hispania.Media.Domain;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.Media.Domain.Timelines;
using Hispania.Media.Domain.Works;

namespace Hispania.Library.Application;

public enum MediaMappingFailureKind
{
    UnsupportedMedia,
    InvalidObservation
}

public abstract record MediaMappingOutcome;

public sealed record MediaMappingSucceeded(MediaCatalogEntry Entry) : MediaMappingOutcome;

public sealed record MediaMappingRejected(MediaMappingFailureKind Kind, string Detail) : MediaMappingOutcome;

public interface IProbedMediaMapper
{
    MediaMappingOutcome Map(DiscoveredMediaCandidate candidate, ProbeSucceeded probe, ImportedPlayableMovieIdentity identity);
}

public sealed class ProbedMediaMapper : IProbedMediaMapper
{
    private const string ObservationNamespace = "ffmpeg";

    public MediaMappingOutcome Map(DiscoveredMediaCandidate candidate, ProbeSucceeded probe, ImportedPlayableMovieIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(identity);

        var container = ContainerCode(probe.Result.ContainerFormatCode);
        if (container is null)
        {
            return new MediaMappingRejected(MediaMappingFailureKind.InvalidObservation, "The probe did not provide a usable container format.");
        }

        var video = probe.Result.Streams.OfType<VideoStreamObservation>()
            .OrderBy(stream => stream.StreamIndex)
            .FirstOrDefault();
        var audio = probe.Result.Streams.OfType<AudioStreamObservation>()
            .OrderBy(stream => stream.StreamIndex)
            .FirstOrDefault();

        if (video is null || audio is null)
        {
            return new MediaMappingRejected(MediaMappingFailureKind.UnsupportedMedia, "An importable movie requires one video stream and one audio stream.");
        }

        try
        {
            return new MediaMappingSucceeded(CreateEntry(candidate, probe.Result.ObservedDuration, container, video, audio, identity));
        }
        catch (DomainValidationException exception)
        {
            return new MediaMappingRejected(MediaMappingFailureKind.InvalidObservation, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return new MediaMappingRejected(MediaMappingFailureKind.InvalidObservation, exception.Message);
        }
    }

    private static MediaCatalogEntry CreateEntry(DiscoveredMediaCandidate candidate, TimeSpan? observedDuration, string container, VideoStreamObservation video, AudioStreamObservation audio, ImportedPlayableMovieIdentity identity)
    {
        var file = candidate.File;
        var work = new Work(identity.WorkId, WorkCategory.Movie);
        var mediaEntity = work.AddMediaEntity(identity.MediaEntityId, MediaEntityCategory.MovieRealisation);
        var timeline = mediaEntity.AddTimeline(identity.TimelineId, observedDuration is null ? null : new TimelineDuration(observedDuration.Value));
        var source = new MediaSource(identity.MediaSourceId, LibrarySourceLocator.From(file));

        var videoAsset = new Asset(identity.MainVideo.AssetId, AssetMediaKind.Video, [AssetRole.Main]);
        var videoRepresentation = videoAsset.AddTechnicalRepresentation(
            identity.MainVideo.TechnicalRepresentationId,
            new VideoTechnicalProfile(
                new PixelDimensions(Required(video.Width, "Video width"), Required(video.Height, "Video height")),
                new CodecIdentifier(ObservationNamespace, RequiredText(video.CodecCode, "Video codec")),
                new FrameRate(RequiredInt(video.FrameRate?.Numerator, "Video frame rate numerator"), RequiredInt(video.FrameRate?.Denominator, "Video frame rate denominator")),
                OptionalPositive(video.BitDepth, "Video bit depth"),
                OptionalBitRate(video.BitRate)));
        videoRepresentation.AddSourceBinding(source, Exposure(container, video.StreamIndex));
        timeline.AddCoverage(AssetCoverage.CreateCovered(identity.MainVideo.AssetCoverageId, timeline.Id, videoAsset.Id, CoverageKind.Full, null, TemporalAlignment.Identity));

        var audioAsset = new Asset(identity.MainAudio.AssetId, AssetMediaKind.Audio, [AssetRole.Main]);
        var audioRepresentation = audioAsset.AddTechnicalRepresentation(
            identity.MainAudio.TechnicalRepresentationId,
            new AudioTechnicalProfile(
                new CodecIdentifier(ObservationNamespace, RequiredText(audio.CodecCode, "Audio codec")),
                Required(audio.SampleRateHertz, "Audio sample rate"),
                Required(audio.ChannelCount, "Audio channel count"),
                OptionalPositive(audio.BitDepth, "Audio bit depth"),
                OptionalBitRate(audio.BitRate)));
        audioRepresentation.AddSourceBinding(source, Exposure(container, audio.StreamIndex));
        timeline.AddCoverage(AssetCoverage.CreateCovered(identity.MainAudio.AssetCoverageId, timeline.Id, audioAsset.Id, CoverageKind.Full, null, TemporalAlignment.Identity));

        var displayName = Path.GetFileNameWithoutExtension(file.RelativePath);
        var media = new ImportedMedia(work, mediaEntity, timeline, videoAsset, audioAsset, source);
        return new MediaCatalogEntry(mediaEntity.Id, displayName, file, new LibrarySourceSnapshot(candidate.ByteLength, candidate.LastModifiedUtc), media);
    }

    private static ContainerExposure Exposure(string container, int streamIndex) =>
        new(new ContainerFormat(ObservationNamespace, container), new StreamLocator(streamIndex));

    private static string? ContainerCode(string? formatCode)
    {
        var codes = formatCode?.Split(',', StringSplitOptions.TrimEntries).Where(code => !string.IsNullOrWhiteSpace(code)).ToArray() ?? [];
        return codes.FirstOrDefault(code => string.Equals(code, "mp4", StringComparison.OrdinalIgnoreCase)) ?? codes.FirstOrDefault();
    }

    private static int Required(int? value, string name) => value is > 0 ? value.Value : throw new ArgumentException($"{name} must be positive.", name);

    private static string RequiredText(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value;

    private static int RequiredInt(long? value, string name) => value is > 0 and <= int.MaxValue ? (int)value.Value : throw new ArgumentException($"{name} must be a positive 32-bit integer.", name);

    private static int? OptionalPositive(int? value, string name) => value is null ? null : Required(value, name);

    private static BitRate? OptionalBitRate(long? value) => value is null ? null : new BitRate(value.Value);
}
