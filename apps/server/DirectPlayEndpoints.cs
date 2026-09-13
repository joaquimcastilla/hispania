using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;
using Hispania.Playback.Contracts;

namespace Hispania.Server;

public static class DirectPlayEndpoints
{
    public static IEndpointRouteBuilder MapDirectPlayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/media", (IMediaCatalogReader catalog) =>
            Results.Json(catalog.GetAll().Select(entry => new MediaListItem(entry.Id.Value, entry.DisplayName))));

        endpoints.MapGet("/api/v1/media/{mediaEntityId}/stream", StreamAsync);
        return endpoints;
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

        if (planning is DirectPlayUnavailable)
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (parsedRange is HttpRangeParseResult.Unsupported)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
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

    private sealed record MediaListItem(Guid Id, string DisplayName);
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
