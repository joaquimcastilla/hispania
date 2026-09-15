using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;

namespace Hispania.Metadata.Tmdb;

public sealed class TmdbArtworkContentResolver(HttpClient client) : IArtworkContentResolver
{
    private const string Cdn = "https://image.tmdb.org/t/p/";
    public string Scheme => "tmdb-image";

    public async ValueTask<ArtworkContentResult> ResolveAsync(ArtworkLocator locator, ArtworkDeliveryVariant variant, CancellationToken cancellationToken = default)
    {
        if (locator.Scheme != "tmdb-image") return new ArtworkContentResult.UnsupportedScheme();
        if (!Valid(locator.OpaqueValue)) return new ArtworkContentResult.InvalidResponse();
        try
        {
            var size = variant == ArtworkDeliveryVariant.PosterCard ? "w500" : "w1280";
            var response = await client.GetAsync(new Uri($"{Cdn}{size}{locator.OpaqueValue}"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { response.Dispose(); return new ArtworkContentResult.NotFound(); }
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) { response.Dispose(); return new ArtworkContentResult.UpstreamUnavailable(); }
            if (!response.IsSuccessStatusCode) { response.Dispose(); return new ArtworkContentResult.InvalidResponse(); }
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/jpeg" or "image/png" or "image/webp")) { response.Dispose(); return new ArtworkContentResult.InvalidResponse(); }
            return new ArtworkContentResult.Success(await response.Content.ReadAsStreamAsync(cancellationToken), type, response.Content.Headers.ContentLength, new ResponseLifetime(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new ArtworkContentResult.Cancelled(); }
        catch (TaskCanceledException) { return new ArtworkContentResult.Timeout(); }
        catch (HttpRequestException) { return new ArtworkContentResult.UpstreamUnavailable(); }
    }
    private static bool Valid(string path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal) && !path.Contains("://", StringComparison.Ordinal) && !path.Any(char.IsControl);
    private sealed class ResponseLifetime(HttpResponseMessage response) : IAsyncDisposable { public ValueTask DisposeAsync() { response.Dispose(); return ValueTask.CompletedTask; } }
}
