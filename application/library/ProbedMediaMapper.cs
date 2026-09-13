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
    MediaMappingOutcome Map(DiscoveredMediaCandidate candidate, ProbeSucceeded probe);
}

public sealed class ProbedMediaMapper : IProbedMediaMapper
{
    private const string ObservationNamespace = "ffmpeg";

    public MediaMappingOutcome Map(DiscoveredMediaCandidate candidate, ProbeSucceeded probe)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(probe);

        if (!IsMp4(probe.Result.ContainerFormatCode))
        {
            return new MediaMappingRejected(MediaMappingFailureKind.UnsupportedMedia, "Only MP4 containers are imported by the 0.1 library mapper.");
        }

        var video = probe.Result.Streams.OfType<VideoStreamObservation>()
            .OrderBy(stream => stream.StreamIndex)
            .FirstOrDefault(IsH264);
        var audio = probe.Result.Streams.OfType<AudioStreamObservation>()
            .OrderBy(stream => stream.StreamIndex)
            .FirstOrDefault(IsAac);

        if (video is null || audio is null)
        {
            return new MediaMappingRejected(MediaMappingFailureKind.UnsupportedMedia, "An importable MP4 requires one H.264 video stream and one AAC audio stream.");
        }

        try
        {
            return new MediaMappingSucceeded(CreateEntry(candidate, probe.Result.ObservedDuration, video, audio));
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

    private static MediaCatalogEntry CreateEntry(DiscoveredMediaCandidate candidate, TimeSpan? observedDuration, VideoStreamObservation video, AudioStreamObservation audio)
    {
        var file = candidate.File;
        var work = new Work(WorkId.New(), WorkCategory.Movie);
        var mediaEntity = work.AddMediaEntity(MediaEntityId.New(), MediaEntityCategory.MovieRealisation);
        var timeline = mediaEntity.AddTimeline(TimelineId.New(), observedDuration is null ? null : new TimelineDuration(observedDuration.Value));
        var source = new MediaSource(MediaSourceId.New(), LibrarySourceLocator.From(file));

        var videoAsset = new Asset(AssetId.New(), AssetMediaKind.Video, [AssetRole.Main]);
        var videoRepresentation = videoAsset.AddTechnicalRepresentation(
            TechnicalRepresentationId.New(),
            new VideoTechnicalProfile(
                new PixelDimensions(Required(video.Width, "Video width"), Required(video.Height, "Video height")),
                new CodecIdentifier(ObservationNamespace, video.CodecCode!),
                new FrameRate(RequiredInt(video.FrameRate?.Numerator, "Video frame rate numerator"), RequiredInt(video.FrameRate?.Denominator, "Video frame rate denominator")),
                Required(video.BitDepth, "Video bit depth"),
                OptionalBitRate(video.BitRate)));
        videoRepresentation.AddSourceBinding(source, Exposure(video.StreamIndex));
        timeline.AddCoverage(AssetCoverage.CreateCovered(AssetCoverageId.New(), timeline.Id, videoAsset.Id, CoverageKind.Full, null, TemporalAlignment.Identity));

        var audioAsset = new Asset(AssetId.New(), AssetMediaKind.Audio, [AssetRole.Main]);
        var audioRepresentation = audioAsset.AddTechnicalRepresentation(
            TechnicalRepresentationId.New(),
            new AudioTechnicalProfile(
                new CodecIdentifier(ObservationNamespace, audio.CodecCode!),
                Required(audio.SampleRateHertz, "Audio sample rate"),
                Required(audio.ChannelCount, "Audio channel count"),
                OptionalPositive(audio.BitDepth, "Audio bit depth"),
                OptionalBitRate(audio.BitRate)));
        audioRepresentation.AddSourceBinding(source, Exposure(audio.StreamIndex));
        timeline.AddCoverage(AssetCoverage.CreateCovered(AssetCoverageId.New(), timeline.Id, audioAsset.Id, CoverageKind.Full, null, TemporalAlignment.Identity));

        var displayName = Path.GetFileNameWithoutExtension(file.RelativePath);
        var media = new ImportedMedia(work, mediaEntity, timeline, videoAsset, audioAsset, source);
        return new MediaCatalogEntry(mediaEntity.Id, displayName, file, new LibrarySourceSnapshot(candidate.ByteLength, candidate.LastModifiedUtc), media);
    }

    private static ContainerExposure Exposure(int streamIndex) =>
        new(new ContainerFormat(ObservationNamespace, "mp4"), new StreamLocator(streamIndex));

    private static bool IsMp4(string? formatCode) => formatCode?.Split(',', StringSplitOptions.TrimEntries).Contains("mp4", StringComparer.OrdinalIgnoreCase) == true;

    private static bool IsH264(VideoStreamObservation stream) => string.Equals(stream.CodecCode, "h264", StringComparison.OrdinalIgnoreCase);

    private static bool IsAac(AudioStreamObservation stream) => string.Equals(stream.CodecCode, "aac", StringComparison.OrdinalIgnoreCase);

    private static int Required(int? value, string name) => value is > 0 ? value.Value : throw new ArgumentException($"{name} must be positive.", name);

    private static int RequiredInt(long? value, string name) => value is > 0 and <= int.MaxValue ? (int)value.Value : throw new ArgumentException($"{name} must be a positive 32-bit integer.", name);

    private static int? OptionalPositive(int? value, string name) => value is null ? null : Required(value, name);

    private static BitRate? OptionalBitRate(long? value) => value is null ? null : new BitRate(value.Value);
}
