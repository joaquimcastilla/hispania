namespace Hispania.Server;

public static class MetadataEnrichmentControlEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEnrichmentControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/admin/metadata/scan", ScanAsync);
        endpoints.MapPost("/api/v1/admin/metadata/scan-new", ScanNewAsync);
        return endpoints;
    }

    private static async Task<IResult> ScanAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var enrichment = services.GetService<AutomaticMovieMetadataEnrichmentHostedService>();
        if (enrichment is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var result = await enrichment.ScanAllLibrariesNowAsync(cancellationToken);
        return result is null
            ? Results.Conflict(new { message = "A metadata scan is already running." })
            : Results.Ok(result);
    }

    private static async Task<IResult> ScanNewAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var enrichment = services.GetService<AutomaticMovieMetadataEnrichmentHostedService>();
        if (enrichment is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var result = await enrichment.ScanNewLibrariesNowAsync(cancellationToken);
        return result is null
            ? Results.Conflict(new { message = "A metadata scan is already running." })
            : Results.Ok(result);
    }
}
