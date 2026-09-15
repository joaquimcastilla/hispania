using System.Net;
using System.Net.Http.Headers;
using Hispania.Media.Domain;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;
using Xunit;

namespace Hispania.Metadata.Tmdb.Tests;

public sealed class TmdbArtworkContentResolverTests
{
    [Theory]
    [InlineData(ArtworkDeliveryVariant.PosterCard, "/poster.jpg", "https://image.tmdb.org/t/p/w500/poster.jpg")]
    [InlineData(ArtworkDeliveryVariant.BackdropDetail, "/backdrop.jpg", "https://image.tmdb.org/t/p/w1280/backdrop.jpg")]
    public async Task UsesCanonicalCdnUrlAndVariantSize(ArtworkDeliveryVariant variant, string path, string expected)
    {
        var handler = new Handler(_ => Image("image/jpeg")); var resolver = new TmdbArtworkContentResolver(new HttpClient(handler));
        var result = Assert.IsType<ArtworkContentResult.Success>(await resolver.ResolveAsync(new ArtworkLocator("tmdb-image", path), variant)); await using var lifetime = result.Lifetime;
        Assert.Equal(expected, handler.Request!.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task StreamsAcceptedImageTypes(string type)
    {
        var bytes = new byte[] { 1, 2, 3 }; var resolver = new TmdbArtworkContentResolver(new HttpClient(new Handler(_ => Image(type, bytes))));
        var result = Assert.IsType<ArtworkContentResult.Success>(await resolver.ResolveAsync(new ArtworkLocator("tmdb-image", "/a.jpg"), ArtworkDeliveryVariant.PosterCard)); await using var lifetime = result.Lifetime;
        using var output = new MemoryStream(); await result.Content.CopyToAsync(output);
        Assert.Equal(type, result.ContentType); Assert.Equal(3, result.ContentLength); Assert.Equal(bytes, output.ToArray());
    }

    [Theory]
    [InlineData("poster.jpg")][InlineData("http://evil.test/a.jpg")][InlineData("https://evil.test/a.jpg")][InlineData("/../secret")][InlineData("/foo/../../bar")]
    public async Task RejectsUnsafeLocatorsBeforeHttp(string path)
    {
        var handler = new Handler(_ => throw new InvalidOperationException()); var resolver = new TmdbArtworkContentResolver(new HttpClient(handler));
        Assert.IsType<ArtworkContentResult.InvalidResponse>(await resolver.ResolveAsync(new ArtworkLocator("tmdb-image", path), ArtworkDeliveryVariant.PosterCard)); Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(404, typeof(ArtworkContentResult.NotFound))][InlineData(429, typeof(ArtworkContentResult.UpstreamUnavailable))][InlineData(500, typeof(ArtworkContentResult.UpstreamUnavailable))]
    public async Task MapsUpstreamStatus(int status, Type expected)
    {
        Assert.IsType(expected, await new TmdbArtworkContentResolver(new HttpClient(new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)))).ResolveAsync(new ArtworkLocator("tmdb-image", "/a.jpg"), ArtworkDeliveryVariant.PosterCard));
    }

    [Fact]
    public async Task RejectsInvalidMimeAndUnsupportedScheme()
    {
        var handler = new Handler(_ => Image("text/html")); var resolver = new TmdbArtworkContentResolver(new HttpClient(handler));
        Assert.IsType<ArtworkContentResult.InvalidResponse>(await resolver.ResolveAsync(new ArtworkLocator("tmdb-image", "/a.jpg"), ArtworkDeliveryVariant.PosterCard));
        Assert.IsType<ArtworkContentResult.UnsupportedScheme>(await resolver.ResolveAsync(new ArtworkLocator("other", "/a.jpg"), ArtworkDeliveryVariant.PosterCard));
    }

    [Fact]
    public async Task CallerCancellationMapsToCancelled()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var result = await new TmdbArtworkContentResolver(new HttpClient(new Handler(_ => Image("image/jpeg")))).ResolveAsync(new ArtworkLocator("tmdb-image", "/a.jpg"), ArtworkDeliveryVariant.PosterCard, cts.Token);
        Assert.IsType<ArtworkContentResult.Cancelled>(result);
    }

    [Fact]
    public async Task InternalHttpTimeoutMapsToTimeoutWhenCallerIsNotCancelled()
    {
        using var caller = new CancellationTokenSource();
        var resolver = new TmdbArtworkContentResolver(new HttpClient(new Handler(_ => throw new TaskCanceledException("timeout"))));
        var result = await resolver.ResolveAsync(new ArtworkLocator("tmdb-image", "/a.jpg"), ArtworkDeliveryVariant.PosterCard, caller.Token);
        Assert.False(caller.IsCancellationRequested); Assert.IsType<ArtworkContentResult.Timeout>(result);
    }

    [Fact]
    public void DomainRejectsEmptyAndControlCharacterLocatorsBeforeResolverCanIssueHttp()
    {
        Assert.Throws<DomainValidationException>(() => new ArtworkLocator("tmdb-image", ""));
        Assert.Throws<DomainValidationException>(() => new ArtworkLocator("tmdb-image", "/a\n.jpg"));
    }

    private static HttpResponseMessage Image(string type, byte[]? bytes = null) { var content = new ByteArrayContent(bytes ?? [1]); content.Headers.ContentType = new MediaTypeHeaderValue(type); return new(HttpStatusCode.OK) { Content = content }; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler { public HttpRequestMessage? Request { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Request = request; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); } }
}
