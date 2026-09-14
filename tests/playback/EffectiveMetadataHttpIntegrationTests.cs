using System.Net;
using System.Text.Json;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class EffectiveMetadataHttpIntegrationTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddMinutes(1);

    [Fact]
    public async Task EndpointUsesWorkProjectionAndReturnsEffectiveWinnersAndProvenance()
    {
        await using var host = await Host.CreateAsync();
        var target = EnrichmentTarget.For(host.Entry.Media.Work.Id);
        var externalTitle = Assertion(target, "title", MetadataValue.Text("TMDB newer"), External("tmdb", "123", Later, .99m));
        var manualTitle = Assertion(target, "title", MetadataValue.Text("Manual older"), Manual(Earlier));
        var descriptionA = Assertion(target, "description", MetadataValue.Text("Old description"), External("tmdb", "123", Earlier), MetadataAssertionStatus.Superseded);
        var descriptionB = Assertion(target, "description", MetadataValue.Text("New description"), External("tmdb", "123", Later));
        var invalidDate = Assertion(target, "release-date", MetadataValue.Text("not-a-date"), External("tmdb", "123", Later));
        var date = Assertion(target, "release-date", MetadataValue.Date(new DateOnly(2024, 1, 2)), External("tmdb", "123", Earlier));
        var poster = Artwork(target, "poster", "poster", External("tmdb", "123", Earlier));
        var backdrop = Artwork(target, "backdrop", "backdrop", Local(Later));
        await host.Store.ApplyAsync(new MetadataEnrichmentWrite([], [externalTitle, manualTitle, descriptionA, descriptionB, invalidDate, date], [poster, backdrop], null));

        using var response = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(host.Entry.Id.Value, root.GetProperty("mediaEntityId").GetGuid());
        Assert.Equal(host.Entry.Media.Work.Id.Value, root.GetProperty("workId").GetGuid());
        Assert.Equal(EffectiveMetadataResolver.PolicyVersion, root.GetProperty("policyVersion").GetString());
        Assert.Equal("Manual older", root.GetProperty("title").GetProperty("value").GetString());
        Assert.Equal("Manual", root.GetProperty("title").GetProperty("origin").GetString());
        Assert.Null(root.GetProperty("title").GetProperty("provider").GetString());
        Assert.Equal("New description", root.GetProperty("description").GetProperty("value").GetString());
        Assert.Equal(descriptionB.Id.Value, root.GetProperty("description").GetProperty("winnerAssertionId").GetGuid());
        Assert.Equal("2024-01-02", root.GetProperty("releaseDate").GetProperty("value").GetString());
        Assert.Equal("tmdb", root.GetProperty("releaseDate").GetProperty("provider").GetString());
        Assert.Equal("tmdb", root.GetProperty("releaseDate").GetProperty("externalIdentifier").GetProperty("provider").GetString());
        var artwork = root.GetProperty("artwork");
        Assert.Equal("poster", artwork.GetProperty("poster").GetProperty("locatorValue").GetString());
        Assert.Equal("tmdb", artwork.GetProperty("poster").GetProperty("provider").GetString());
        Assert.Equal("backdrop", artwork.GetProperty("backdrop").GetProperty("locatorValue").GetString());
        Assert.Equal("LocalProcess", artwork.GetProperty("backdrop").GetProperty("origin").GetString());
        Assert.Equal("NoEligibleArtworkReference", artwork.GetProperty("logo").GetProperty("resolutionReason").GetString());
    }

    [Fact]
    public async Task EndpointReturnsNotFoundForUnknownMediaAndEmptyProjectionForKnownMedia()
    {
        await using var host = await Host.CreateAsync();
        using var missing = await host.Client.GetAsync($"/api/v1/media/{Guid.NewGuid()}/metadata");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var existing = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/metadata");
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        using var json = JsonDocument.Parse(await existing.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("title").GetProperty("value").ValueKind);
        Assert.Equal("NoEligibleAssertion", json.RootElement.GetProperty("title").GetProperty("resolutionReason").GetString());
    }

    [Fact]
    public async Task RestartedHostReturnsTheSameEffectiveMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-effective-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var entry = DirectPlayPlannerTests.Entry();
            await using (var first = await Host.CreateAsync(directory, entry))
            {
                var target = EnrichmentTarget.For(entry.Media.Work.Id);
                await first.Store.ApplyAsync(new MetadataEnrichmentWrite([], [Assertion(target, "title", MetadataValue.Text("TMDB title"), External("tmdb", "123", Earlier))], [Artwork(target, "poster", "poster", External("tmdb", "123", Earlier))], null));
                var initial = await first.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata");
                await first.DisposeAsync();
                await using var restarted = await Host.CreateAsync(directory, entry);
                var after = await restarted.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata");
                Assert.Equal(initial, after);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MovieProviderWorkflowFeedsTheEffectiveMetadataEndpointWithoutNetwork()
    {
        await using var host = await Host.CreateAsync();
        var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("tmdb"), new ExternalIdentifierNamespace("movie"), "123");
        var document = new NormalizedMovieDocument(identifier, [],
            [new(new MetadataField("title"), MetadataValue.Text("TMDB title"), null), new(new MetadataField("original-title"), MetadataValue.Text("TMDB original"), null), new(new MetadataField("description"), MetadataValue.Text("TMDB description"), null), new(new MetadataField("release-date"), MetadataValue.Date(new DateOnly(2021, 1, 1)), null)],
            [new(new ArtworkRole("poster"), new ArtworkLocator("tmdb-image", "/poster.jpg"), null), new(new ArtworkRole("backdrop"), new ArtworkLocator("tmdb-image", "/backdrop.jpg"), null)], Earlier, "tmdb-movie-v1", "test-fingerprint");
        var service = new MovieMetadataApplicationService(new FakeMovieProvider(identifier, document), new ExistingWorkStore(host.Store));
        await service.SelectMovieMetadataCandidateAsync(host.Entry.Media.Work.Id, identifier, Earlier);
        var association = Assert.Single((await host.Store.LoadAsync()).Associations).Id;
        await service.ConfirmMovieMetadataCandidateAsync(association, Later);
        Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await service.FetchAndApplyMovieMetadataAsync(association));

        using var response = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("TMDB title", root.GetProperty("title").GetProperty("value").GetString());
        Assert.Equal("TMDB original", root.GetProperty("originalTitle").GetProperty("value").GetString());
        Assert.Equal("TMDB description", root.GetProperty("description").GetProperty("value").GetString());
        Assert.Equal("2021-01-01", root.GetProperty("releaseDate").GetProperty("value").GetString());
        Assert.Equal("/poster.jpg", root.GetProperty("artwork").GetProperty("poster").GetProperty("locatorValue").GetString());
        Assert.Equal("/backdrop.jpg", root.GetProperty("artwork").GetProperty("backdrop").GetProperty("locatorValue").GetString());
        Assert.Equal("tmdb", root.GetProperty("title").GetProperty("provider").GetString());
        Assert.Equal(EffectiveMetadataResolver.PolicyVersion, root.GetProperty("policyVersion").GetString());
    }

    private static MetadataAssertion Assertion(EnrichmentTarget target, string field, MetadataValue value, MetadataProvenance provenance, MetadataAssertionStatus status = MetadataAssertionStatus.Active)
    {
        var id = MetadataAssertionId.New();
        return new MetadataAssertion(id, target, new MetadataField(field), value, provenance, status, status == MetadataAssertionStatus.Superseded ? MetadataAssertionId.New() : null);
    }

    private static ArtworkReference Artwork(EnrichmentTarget target, string role, string locator, MetadataProvenance provenance) => new(ArtworkReferenceId.New(), target, new ArtworkRole(role), new ArtworkLocator("tmdb-image", locator), provenance);
    private static MetadataProvenance Manual(DateTimeOffset at) => new(MetadataProvenanceOrigin.Manual, at);
    private static MetadataProvenance Local(DateTimeOffset at) => new(MetadataProvenanceOrigin.LocalProcess, at);
    private static MetadataProvenance External(string provider, string value, DateTimeOffset at, decimal? confidence = null)
    {
        var identifier = new ExternalIdentifier(new ExternalIdentifierProvider(provider), new ExternalIdentifierNamespace("movie"), value);
        return new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, at, identifier.Provider, identifier, confidence);
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
        public ValueTask<bool> ApplyAsync(MetadataEnrichmentWrite write, CancellationToken cancellationToken = default) => inner.ApplyAsync(write, cancellationToken);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly string directory;
        private bool disposed;
        private Host(WebApplication app, HttpClient client, IMetadataEnrichmentStore store, MediaCatalogEntry entry, string directory) { this.app = app; Client = client; Store = store; Entry = entry; this.directory = directory; }
        public HttpClient Client { get; }
        public IMetadataEnrichmentStore Store { get; }
        public MediaCatalogEntry Entry { get; }

        public static Task<Host> CreateAsync() => CreateAsync(Path.Combine(Path.GetTempPath(), "hispania-effective-http", Guid.NewGuid().ToString("N")), DirectPlayPlannerTests.Entry());

        public static async Task<Host> CreateAsync(string directory, MediaCatalogEntry entry)
        {
            Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "metadata.sqlite");
            var catalog = new InMemoryMediaCatalog(); catalog.Upsert(entry);
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddSqliteLibraryPersistence(database);
            builder.Services.AddSingleton<IMediaCatalogReader>(catalog);
            builder.Services.AddSingleton<IEffectiveMetadataResolver, EffectiveMetadataResolver>();
            builder.Services.AddSingleton<IEffectiveMetadataProjectionReader, EffectiveMetadataProjectionReader>();
            var app = builder.Build();
            await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(app.Services.GetRequiredService<EfLibraryCatalogStore>(), default);
            app.MapEffectiveMetadataEndpoints();
            await app.StartAsync();
            return new Host(app, app.GetTestClient(), app.Services.GetRequiredService<IMetadataEnrichmentStore>(), entry, directory);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
