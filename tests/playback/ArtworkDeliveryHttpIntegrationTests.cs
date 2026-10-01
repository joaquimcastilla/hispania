using System.Net;
using System.Net.Http.Headers;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class ArtworkDeliveryHttpIntegrationTests
{
    [Fact]
    public async Task FakeTmdbMetadataFlowsThroughEffectiveMetadataHispaniaUrlAndFakeCdn()
    {
        var handler = new Handler(_ => Image("image/jpeg", [7, 8, 9]));
        await using var host = await Host.CreateAsync(handler);
        var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("tmdb"), new ExternalIdentifierNamespace("movie"), "123");
        var observedAt = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
        var document = new NormalizedMovieDocument(identifier, [],
            [new(new MetadataField("title"), MetadataValue.Text("Fake TMDB title"), null)],
            [new(new ArtworkRole("poster"), new ArtworkLocator("tmdb-image", "/poster.jpg"), null), new(new ArtworkRole("backdrop"), new ArtworkLocator("tmdb-image", "/backdrop.jpg"), null)],
            observedAt, "tmdb-movie-v1", "fake-fingerprint");
        var application = new MovieMetadataApplicationService(new FakeMovieProvider(identifier, document), new ExistingWorkStore(host.Store));

        await application.SelectMovieMetadataCandidateAsync(host.Entry.Media.Work.Id, identifier, observedAt);
        var association = Assert.Single((await host.Store.LoadAsync()).Associations).Id;
        await application.ConfirmMovieMetadataCandidateAsync(association, observedAt);
        Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await application.FetchAndApplyMovieMetadataAsync(association));
        var poster = Assert.Single((await host.Store.LoadAsync()).ArtworkReferences, reference => reference.Role.Code == "poster");

        using var listResponse = await host.Client.GetAsync("/api/v1/media");
        using var list = System.Text.Json.JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var posterUrl = list.RootElement[0].GetProperty("effective").GetProperty("posterUrl").GetString();
        Assert.Equal($"/api/v1/artwork/{poster.Id.Value}?variant=poster-card", posterUrl);

        using var artworkResponse = await host.Client.GetAsync(posterUrl!);
        Assert.Equal(HttpStatusCode.OK, artworkResponse.StatusCode);
        Assert.Equal("image/jpeg", artworkResponse.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=86400", artworkResponse.Headers.CacheControl!.ToString());
        Assert.Equal([7, 8, 9], await artworkResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", handler.Request!.RequestUri!.AbsoluteUri);

        using var detailResponse = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/metadata");
        using var detail = System.Text.Json.JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var artwork = detail.RootElement.GetProperty("artwork");
        Assert.Equal($"/api/v1/artwork/{poster.Id.Value}?variant=poster-card", artwork.GetProperty("poster").GetProperty("deliveryUrl").GetString());
        Assert.Equal("tmdb-image", artwork.GetProperty("poster").GetProperty("locatorScheme").GetString());
        Assert.Equal("/poster.jpg", artwork.GetProperty("poster").GetProperty("locatorValue").GetString());
        Assert.Equal("tmdb", artwork.GetProperty("poster").GetProperty("provider").GetString());
        var backdrop = Assert.Single((await host.Store.LoadAsync()).ArtworkReferences, reference => reference.Role.Code == "backdrop");
        Assert.Equal($"/api/v1/artwork/{backdrop.Id.Value}?variant=backdrop-detail", artwork.GetProperty("backdrop").GetProperty("deliveryUrl").GetString());
        Assert.Equal("tmdb-image", artwork.GetProperty("backdrop").GetProperty("locatorScheme").GetString());
        Assert.Equal("tmdb", artwork.GetProperty("backdrop").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task DefaultPosterStreamsBytesWithCacheControl()
    {
        var handler = new Handler(_ => Image("image/jpeg", [1, 2, 3]));
        await using var host = await Host.CreateAsync(handler);
        var artwork = await host.PersistAsync("tmdb-image", "/poster.jpg");

        using var response = await host.Client.GetAsync($"/api/v1/artwork/{artwork.Id.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(3, response.Content.Headers.ContentLength);
        Assert.Equal("public, max-age=86400", response.Headers.CacheControl!.ToString());
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", handler.Request!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task BackdropVariantUsesW1280()
    {
        var handler = new Handler(_ => Image("image/png", [4]));
        await using var host = await Host.CreateAsync(handler);
        var artwork = await host.PersistAsync("tmdb-image", "/backdrop.jpg");

        using var response = await host.Client.GetAsync($"/api/v1/artwork/{artwork.Id.Value}?variant=backdrop-detail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/backdrop.jpg", handler.Request!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task UnknownVariantReturnsBadRequestWithoutAnUpstreamRequest()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));
        await using var host = await Host.CreateAsync(handler);
        var artwork = await host.PersistAsync("tmdb-image", "/poster.jpg");

        using var response = await host.Client.GetAsync($"/api/v1/artwork/{artwork.Id.Value}?variant=arbitrary-size");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task MissingArtworkReturnsNotFound()
    {
        await using var host = await Host.CreateAsync(new Handler(_ => Image("image/jpeg")));
        using var response = await host.Client.GetAsync($"/api/v1/artwork/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedSchemeReturnsUnprocessableEntityWithoutAnUpstreamRequest()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));
        await using var host = await Host.CreateAsync(handler);
        var artwork = await host.PersistAsync("other-image", "/poster.jpg");

        using var response = await host.Client.GetAsync($"/api/v1/artwork/{artwork.Id.Value}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(404, HttpStatusCode.NotFound, "image/jpeg")]
    [InlineData(500, HttpStatusCode.BadGateway, "image/jpeg")]
    [InlineData(200, HttpStatusCode.BadGateway, "text/html")]
    public async Task MapsUpstreamFailures(int upstreamStatus, HttpStatusCode expected, string contentType)
    {
        await using var host = await Host.CreateAsync(new Handler(_ => Image(contentType, [1], (HttpStatusCode)upstreamStatus)));
        var artwork = await host.PersistAsync("tmdb-image", "/poster.jpg");

        using var response = await host.Client.GetAsync($"/api/v1/artwork/{artwork.Id.Value}");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task TimeoutReturnsGatewayTimeout()
    {
        await using var host = await Host.CreateAsync(new Handler(_ => throw new TaskCanceledException("upstream timeout")));
        var artwork = await host.PersistAsync("tmdb-image", "/poster.jpg");

        using var response = await host.Client.GetAsync($"/api/v1/artwork/{artwork.Id.Value}");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    private static HttpResponseMessage Image(string contentType, byte[]? bytes = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes ?? [1]);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly string directory;
        private bool disposed;

        private Host(WebApplication application, HttpClient client, IMetadataEnrichmentStore store, MediaCatalogEntry entry, string directory)
        {
            this.application = application;
            Client = client;
            Store = store;
            Entry = entry;
            this.directory = directory;
        }

        public HttpClient Client { get; }
        public IMetadataEnrichmentStore Store { get; }
        public MediaCatalogEntry Entry { get; }

        public static async Task<Host> CreateAsync(HttpMessageHandler handler)
        {
            var directory = Path.Combine(Path.GetTempPath(), "hispania-artwork-http", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSqliteLibraryPersistence(Path.Combine(directory, "metadata.sqlite"));
            var entry = DirectPlayPlannerTests.Entry();
            var catalog = new InMemoryMediaCatalog();
            catalog.Upsert(entry);
            builder.Services.AddSingleton<IMediaCatalogReader>(catalog);
            builder.Services.AddSingleton<IEffectiveMetadataResolver, EffectiveMetadataResolver>();
            builder.Services.AddSingleton<IEffectiveMetadataProjectionReader, EffectiveMetadataProjectionReader>();
            DomainAuthorizationTestHost.AddAuthorizedUser(builder.Services);
            builder.Services.AddSingleton<IArtworkContentResolver>(new TmdbArtworkContentResolver(new HttpClient(handler)));
            var application = builder.Build();
            await application.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(application.Services.GetRequiredService<EfLibraryCatalogStore>(), default);
            application.MapMediaListEndpoint();
            application.MapEffectiveMetadataEndpoints();
            application.MapArtworkDeliveryEndpoints();
            await application.StartAsync();
            var client = application.GetTestClient();
            DomainAuthorizationTestHost.Authenticate(client);
            return new Host(application, client, application.Services.GetRequiredService<IMetadataEnrichmentStore>(), entry, directory);
        }

        public async Task<ArtworkReference> PersistAsync(string scheme, string path)
        {
            var target = EnrichmentTarget.For(WorkId.New());
            var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("tmdb"), new ExternalIdentifierNamespace("movie"), "123");
            var artwork = new ArtworkReference(ArtworkReferenceId.New(), target, new ArtworkRole("poster"), new ArtworkLocator(scheme, path), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, DateTimeOffset.UtcNow, identifier.Provider, identifier));
            await Store.ApplyAsync(new MetadataEnrichmentWrite([], [], [artwork], null));
            return artwork;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeMovieProvider(ExternalIdentifier identifier, NormalizedMovieDocument document) : IMovieMetadataProvider
    {
        public MovieMetadataProviderDescriptor Descriptor { get; } = new(identifier.Provider, "Fake TMDB", [identifier.Namespace], "tmdb-movie-v1");
        public ValueTask<MovieProviderResult> SearchAsync(MovieSearchRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<MovieProviderResult>(new MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>([]));
        public ValueTask<MovieProviderResult> GetMovieAsync(ExternalIdentifier requested, CancellationToken cancellationToken = default) => ValueTask.FromResult<MovieProviderResult>(requested == identifier ? new MovieProviderSuccess<NormalizedMovieDocument>(document) : new MovieProviderUnsupported("Unknown identifier."));
    }

    private sealed class ExistingWorkStore(IMetadataEnrichmentStore inner) : IMetadataEnrichmentStore
    {
        public ValueTask<bool> WorkExistsAsync(WorkId workId, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<MetadataEnrichmentSnapshot> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public ValueTask<MetadataEnrichmentTargetSnapshot> LoadForTargetAsync(EnrichmentTarget target, CancellationToken cancellationToken = default) => inner.LoadForTargetAsync(target, cancellationToken);
        public ValueTask<IReadOnlyCollection<MetadataEnrichmentTargetSnapshot>> LoadForTargetsAsync(IReadOnlyCollection<EnrichmentTarget> targets, CancellationToken cancellationToken = default) => inner.LoadForTargetsAsync(targets, cancellationToken);
        public ValueTask<ArtworkReference?> GetArtworkReferenceByIdAsync(ArtworkReferenceId id, CancellationToken cancellationToken = default) => inner.GetArtworkReferenceByIdAsync(id, cancellationToken);
        public ValueTask<bool> ApplyAsync(MetadataEnrichmentWrite write, CancellationToken cancellationToken = default) => inner.ApplyAsync(write, cancellationToken);
    }
}
