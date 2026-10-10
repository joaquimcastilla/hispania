using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;
using Hispania.Playback.Contracts;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Media.Domain.TechnicalRepresentations;

namespace Hispania.Server;

public static class DirectPlayEndpoints
{
    public static IEndpointRouteBuilder MapDirectPlayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMediaListEndpoint();
        endpoints.MapGet("/api/v1/media/{mediaEntityId}/stream", StreamAsync)
            .RequirePlatformPermission(MediaPermissionIds.PlaybackUse, allowLegacyAdminBearer: false);
        return endpoints;
    }

    public static IEndpointRouteBuilder MapMediaListEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/media", ListAsync)
            .RequirePlatformPermission(MediaPermissionIds.LibraryRead, allowLegacyAdminBearer: false);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(IMediaCatalogReader catalog, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entries = catalog.GetAll();
        var targets = entries.Select(entry => EnrichmentTarget.For(entry.Media.Work.Id)).ToArray();
        var reader = services.GetService(typeof(IEffectiveMetadataProjectionReader)) as IEffectiveMetadataProjectionReader;
        var projections = reader is null ? [] : await reader.ReadManyAsync(targets, cancellationToken);
        var byTarget = projections.ToDictionary(x => x.Target);
        return Results.Json(entries.Select(entry =>
        {
            var projection = byTarget.GetValueOrDefault(EnrichmentTarget.For(entry.Media.Work.Id));
            return new MediaListItem(entry.Id.Value, entry.DisplayName, new EffectiveMediaListSummary(
                projection?.Title.Value,
                projection?.ReleaseDate.Value,
                projection is null ? null : EffectiveArtworkLocator.From(projection.Poster),
                projection is null ? null : ArtworkDeliveryUrl.For(projection.Poster, ArtworkDeliveryVariant.PosterCard)),
                Technical(entry));
        }));
    }

    private static MediaTechnicalSummary Technical(MediaCatalogEntry entry)
    {
        var video = entry.Media.VideoAsset.TechnicalRepresentations.FirstOrDefault(x => x.Profile is VideoTechnicalProfile);
        var videoProfile = video?.Profile as VideoTechnicalProfile;
        var container = video?.SourceBindings.FirstOrDefault(x => x.MediaSourceId == entry.Media.Source.Id)?.ContainerExposure?.ContainerFormat.Code;
        var audioTracks = entry.Media.AudioAsset.TechnicalRepresentations
            .Where(x => x.Profile is AudioTechnicalProfile)
            .Select(x => new { Representation = x, Profile = (AudioTechnicalProfile)x.Profile })
            .Select(x => new AudioTrackSummary(
                x.Representation.Id.Value.ToString(),
                x.Representation.SourceBindings.FirstOrDefault(binding => binding.MediaSourceId == entry.Media.Source.Id)?.ContainerExposure?.StreamLocator.StreamIndex,
                x.Profile.Codec.Code,
                x.Profile.ChannelCount,
                x.Profile.SampleRateHertz))
            .ToArray();
        return new MediaTechnicalSummary(
            entry.Media.Timeline.Duration?.Value.TotalSeconds,
            container,
            videoProfile is null ? null : new VideoTrackSummary(videoProfile.Codec.Code, videoProfile.Dimensions.Width, videoProfile.Dimensions.Height, videoProfile.BitDepth),
            audioTracks);
    }

    private static async Task StreamAsync(HttpContext context, string mediaEntityId, IPlaybackPlanner planner, IDirectPlayExecutor executor)
    {
        if (!Guid.TryParse(mediaEntityId, out var value) || value == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var parsedRange = HttpByteRangeParser.Parse(context.Request.Headers.Range);
        if (parsedRange is HttpRangeParseResult.Invalid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var id = new MediaEntityId(value);
        var planning = planner.Plan(id);
        if (planning is PlaybackMediaNotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (parsedRange is HttpRangeParseResult.Unsupported)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            return;
        }

        if (planning is DirectPlayUnavailable)
        {
            var transcoder = context.RequestServices.GetService<ICompatibleMediaTranscoderV1>();
            var outcome = transcoder is null ? new CompatibleTranscodeUnavailable() : await transcoder.CreateCompatibleRepresentationAsync(id, context.RequestAborted).ConfigureAwait(false);
            if (outcome is not CompatibleTranscodeReady compatibleFile)
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            var contentStore = context.RequestServices.GetService<ITranscodedContentStore>();
            var generated = contentStore is null ? null : await contentStore.OpenReadAsync(compatibleFile.Content, context.RequestAborted).ConfigureAwait(false);
            if (generated is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await using (generated.ConfigureAwait(false))
            {
                context.Response.Headers.AcceptRanges = "bytes";
                await Results.File(generated.Stream, compatibleFile.ContentType, enableRangeProcessing: true).ExecuteAsync(context).ConfigureAwait(false);
            }
            return;
        }

        var plan = ((DirectPlayPlanned)planning).Plan;
        await using var result = await executor.ExecuteAsync(plan, ((HttpRangeParseResult.Parsed)parsedRange).Request, context.RequestAborted).ConfigureAwait(false);
        await WriteExecutionAsync(context, result).ConfigureAwait(false);
    }

    private static async Task WriteExecutionAsync(HttpContext context, DirectPlayExecutionResult result)
    {
        switch (result)
        {
            case FullContent full:
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.ContentType = "video/mp4";
                context.Response.ContentLength = full.TotalLength;
                await full.Content.Stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
                break;
            case PartialContent partial:
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.ContentType = "video/mp4";
                context.Response.ContentLength = partial.SelectedLength;
                context.Response.Headers.ContentRange = $"bytes {partial.Start}-{partial.End}/{partial.TotalLength}";
                await partial.Content.Stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
                break;
            case RangeNotSatisfiable range:
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.Headers.ContentRange = $"bytes */{range.TotalLength}";
                break;
            case RangeNotSupported:
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                break;
            case SourceUnavailable:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
            case SourceChanged or PlanNoLongerValid:
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                break;
        }
    }

    public sealed record MediaListItem(Guid Id, string DisplayName, EffectiveMediaListSummary Effective, MediaTechnicalSummary Technical);
    public sealed record EffectiveMediaListSummary(string? Title, DateOnly? ReleaseDate, EffectiveArtworkLocator? Poster, string? PosterUrl);
    public sealed record MediaTechnicalSummary(double? DurationSeconds, string? Container, VideoTrackSummary? Video, IReadOnlyCollection<AudioTrackSummary> AudioTracks);
    public sealed record VideoTrackSummary(string Codec, int Width, int Height, int? BitDepth);
    public sealed record AudioTrackSummary(string RepresentationId, int? StreamIndex, string Codec, int Channels, int SampleRateHertz);
    public sealed record EffectiveArtworkLocator(string Scheme, string Value)
    {
        internal static EffectiveArtworkLocator? From(EffectiveArtworkSelection selection) => selection.Locator is null ? null : new(selection.Locator.Scheme, selection.Locator.OpaqueValue);
    }
}

internal abstract record HttpRangeParseResult
{
    internal sealed record Parsed(ByteRangeRequest Request) : HttpRangeParseResult;
    internal sealed record Invalid : HttpRangeParseResult;
    internal sealed record Unsupported : HttpRangeParseResult;
}

internal static class HttpByteRangeParser
{
    internal static HttpRangeParseResult Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return new HttpRangeParseResult.Parsed(new ByteRangeRequest.Full());
        var parts = header.Trim().Split('=', 2);
        if (parts.Length != 2 || !string.Equals(parts[0].Trim(), "bytes", StringComparison.OrdinalIgnoreCase)) return new HttpRangeParseResult.Invalid();
        var value = parts[1].Trim();
        if (value.Contains(',', StringComparison.Ordinal)) return new HttpRangeParseResult.Unsupported();
        var bounds = value.Split('-', 2);
        if (bounds.Length != 2) return new HttpRangeParseResult.Invalid();
        var left = bounds[0].Trim(); var right = bounds[1].Trim();
        if (left.Length == 0 && right.Length == 0) return new HttpRangeParseResult.Invalid();
        if (left.Length == 0) return TryPositive(right, out var suffix) ? new HttpRangeParseResult.Parsed(new ByteRangeRequest.Suffix(suffix)) : new HttpRangeParseResult.Invalid();
        if (!TryNonNegative(left, out var start)) return new HttpRangeParseResult.Invalid();
        if (right.Length == 0) return new HttpRangeParseResult.Parsed(new ByteRangeRequest.From(start));
        return TryNonNegative(right, out var end) ? new HttpRangeParseResult.Parsed(new ByteRangeRequest.Closed(start, end)) : new HttpRangeParseResult.Invalid();
    }

    private static bool TryNonNegative(string value, out long number) => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number) && number >= 0;
    private static bool TryPositive(string value, out long number) => TryNonNegative(value, out number) && number > 0;
}
