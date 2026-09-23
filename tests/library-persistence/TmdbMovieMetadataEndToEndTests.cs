using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class TmdbMovieMetadataEndToEndTests
{
    [Fact]
    public async Task RealTmdbAdapterPersistsEnrichmentAndRehydratesAcrossRestart()
    {
        var path = NewPath(); var work = WorkId.New(); var handler = new TmdbHandler(Details("Version A", "/poster-a.jpg"));
        ExternalIdentifierAssociationId associationId; IReadOnlyCollection<Guid> assertionIds; IReadOnlyCollection<Guid> artworkIds; string fingerprint;
        await using (var services = await CreateServicesAsync(path, work))
        {
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            var provider = Provider(handler); var application = new MovieMetadataApplicationService(provider, store);
            var search = Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await application.SearchMovieMetadataAsync(new("Seduction & Snacks", 2021, null)));
            var candidate = Assert.Single(search.Value);
            Assert.Equal("tmdb", candidate.ExternalIdentifier.Provider.Value); Assert.Equal("movie", candidate.ExternalIdentifier.Namespace.Value); Assert.Equal("123", candidate.ExternalIdentifier.Value);
            Assert.Equal("Seduction and Snacks", candidate.Title); Assert.Equal("Seduction & Snacks", candidate.OriginalTitle); Assert.Equal(new DateOnly(2021, 1, 1), candidate.ReleaseDate);
            Assert.Empty((await store.LoadAsync()).Associations);

            await application.SelectMovieMetadataCandidateAsync(work, candidate.ExternalIdentifier, DateTimeOffset.UtcNow);
            var selected = Assert.Single((await store.LoadAsync()).Associations); associationId = selected.Id;
            Assert.Equal(EnrichmentTargetKind.Work, selected.Target.Kind); Assert.Equal(ExternalIdentifierAssociationStatus.Candidate, selected.CurrentStatus);
            await application.ConfirmMovieMetadataCandidateAsync(associationId, DateTimeOffset.UtcNow);
            var confirmed = Assert.Single((await store.LoadAsync()).Associations); Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, confirmed.CurrentStatus); Assert.Equal(2, confirmed.LifecycleEntries.Count);

            var result = Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await application.FetchAndApplyMovieMetadataAsync(associationId)); fingerprint = result.Value.PayloadFingerprint;
            var applied = await store.LoadAsync();
            Assert.Equal(4, applied.Assertions.Count); Assert.Equal(2, applied.ArtworkReferences.Count);
            Assert.All(applied.Assertions, x => { Assert.Equal(EnrichmentTarget.For(work), x.Target); Assert.Equal(MetadataProvenanceOrigin.ExternalProvider, x.Provenance.Origin); Assert.Equal("tmdb", x.Provenance.Provider!.Value); Assert.Equal(candidate.ExternalIdentifier, x.Provenance.RelatedExternalIdentifier); Assert.Null(x.Provenance.Confidence); });
            Assert.Contains(applied.ArtworkReferences, x => x.Role.Code == "poster" && x.Locator.Scheme == "tmdb-image" && x.Locator.OpaqueValue == "/poster-a.jpg");
            Assert.Contains(applied.ArtworkReferences, x => x.Role.Code == "backdrop" && x.Locator.Scheme == "tmdb-image" && x.Locator.OpaqueValue == "/backdrop.jpg");
            var projection = await new EffectiveMetadataProjectionReader(store, new EffectiveMetadataResolver()).ReadAsync(EnrichmentTarget.For(work));
            Assert.Equal("Seduction and Snacks", projection.Title.Value); Assert.Equal("Seduction & Snacks", projection.OriginalTitle.Value); Assert.Equal("Version A", projection.Description.Value); Assert.Equal(new DateOnly(2021, 1, 1), projection.ReleaseDate.Value);
            Assert.Equal("/poster-a.jpg", projection.Poster.Locator!.OpaqueValue); Assert.Equal("/backdrop.jpg", projection.Backdrop.Locator!.OpaqueValue);
            var refresh = Assert.Single(applied.RefreshStates); Assert.Equal(MetadataRefreshStatus.Succeeded, refresh.Status); Assert.Equal("tmdb-movie-v2", refresh.MappingVersion); Assert.Equal(fingerprint, refresh.PayloadFingerprint); Assert.NotNull(refresh.LastSuccessAt);
            assertionIds = applied.Assertions.Select(x => x.Id.Value).ToArray(); artworkIds = applied.ArtworkReferences.Select(x => x.Id.Value).ToArray();
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            var repeated = await store.LoadAsync(); Assert.Equal(assertionIds.Order(), repeated.Assertions.Select(x => x.Id.Value).Order()); Assert.Equal(artworkIds.Order(), repeated.ArtworkReferences.Select(x => x.Id.Value).Order());
        }
        await using var restarted = await CreateServicesAsync(path, null);
        var rehydrated = await restarted.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync();
        var association = Assert.Single(rehydrated.Associations); Assert.Equal(associationId, association.Id); Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, association.CurrentStatus); Assert.Equal(2, association.LifecycleEntries.Count);
        Assert.Equal(assertionIds.Order(), rehydrated.Assertions.Select(x => x.Id.Value).Order()); Assert.Equal(artworkIds.Order(), rehydrated.ArtworkReferences.Select(x => x.Id.Value).Order());
        Assert.Equal(fingerprint, Assert.Single(rehydrated.RefreshStates).PayloadFingerprint);
        var restartedProjection = await new EffectiveMetadataProjectionReader(restarted.GetRequiredService<IMetadataEnrichmentStore>(), new EffectiveMetadataResolver()).ReadAsync(EnrichmentTarget.For(work));
        Assert.Equal("Seduction and Snacks", restartedProjection.Title.Value); Assert.Equal("/poster-a.jpg", restartedProjection.Poster.Locator!.OpaqueValue);
    }

    [Fact]
    public async Task ChangedPayloadSupersedesDescriptionAndAddsArtworkDelta()
    {
        var path = NewPath(); var work = WorkId.New(); var handler = new TmdbHandler(Details("Version A", "/poster-a.jpg"));
        await using var services = await CreateServicesAsync(path, work); var store = services.GetRequiredService<IMetadataEnrichmentStore>(); var application = new MovieMetadataApplicationService(Provider(handler), store);
        var id = Assert.Single(Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await application.SearchMovieMetadataAsync(new("Seduction", 2021, null))).Value).ExternalIdentifier;
        var association = await ConfirmAsync(application, store, work, id); await application.FetchAndApplyMovieMetadataAsync(association);
        handler.DetailsPayload = Details("Version B", "/poster-b.jpg"); await application.FetchAndApplyMovieMetadataAsync(association);
        var snapshot = await store.LoadAsync(); var descriptions = snapshot.Assertions.Where(x => x.Field.Code == "description").ToArray();
        Assert.Single(descriptions, x => x.Status == MetadataAssertionStatus.Active && x.Value == MetadataValue.Text("Version B")); Assert.Single(descriptions, x => x.Status == MetadataAssertionStatus.Superseded && x.Value == MetadataValue.Text("Version A"));
        Assert.Contains(snapshot.ArtworkReferences, x => x.Locator.OpaqueValue == "/poster-a.jpg"); Assert.Contains(snapshot.ArtworkReferences, x => x.Locator.OpaqueValue == "/poster-b.jpg");
    }

    [Fact]
    public async Task DetailsFailureAndCancellationDoNotPartiallyEnrich()
    {
        var path = NewPath(); var work = WorkId.New(); var handler = new TmdbHandler(null) { DetailsStatus = HttpStatusCode.InternalServerError };
        await using var services = await CreateServicesAsync(path, work); var store = services.GetRequiredService<IMetadataEnrichmentStore>(); var application = new MovieMetadataApplicationService(Provider(handler), store);
        var id = Assert.Single(Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await application.SearchMovieMetadataAsync(new("Seduction", 2021, null))).Value).ExternalIdentifier;
        var association = await ConfirmAsync(application, store, work, id);
        Assert.IsType<MovieProviderUnavailable>(await application.FetchAndApplyMovieMetadataAsync(association)); var failed = await store.LoadAsync(); Assert.Empty(failed.Assertions); Assert.Empty(failed.ArtworkReferences); Assert.Equal(MetadataRefreshStatus.Failed, Assert.Single(failed.RefreshStates).Status); Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, Assert.Single(failed.Associations).CurrentStatus);
        handler.CancelDetails = true; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application.FetchAndApplyMovieMetadataAsync(association, cancellation.Token).AsTask());
        var cancelled = await store.LoadAsync(); Assert.Empty(cancelled.Assertions); Assert.Empty(cancelled.ArtworkReferences); Assert.DoesNotContain(cancelled.RefreshStates, x => x.Status == MetadataRefreshStatus.Succeeded);
    }

    private static async Task<ExternalIdentifierAssociationId> ConfirmAsync(MovieMetadataApplicationService application, IMetadataEnrichmentStore store, WorkId work, ExternalIdentifier id) { await application.SelectMovieMetadataCandidateAsync(work, id, DateTimeOffset.UtcNow); var association = Assert.Single((await store.LoadAsync()).Associations).Id; await application.ConfirmMovieMetadataCandidateAsync(association, DateTimeOffset.UtcNow); return association; }
    private static TmdbMovieMetadataProvider Provider(TmdbHandler handler) { var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(5) }; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token"); return new TmdbMovieMetadataProvider(client, new TmdbMovieMetadataProviderOptions { Enabled = true, ReadAccessToken = "test-token" }); }
    private const string Search = """{"page":1,"results":[{"id":123,"title":"Seduction and Snacks","original_title":"Seduction & Snacks","release_date":"2021-01-01","popularity":1}]}""";
    private static string Details(string overview, string poster) => $$"""{"id":123,"title":"Seduction and Snacks","original_title":"Seduction & Snacks","overview":"{{overview}}","release_date":"2021-01-01","poster_path":"{{poster}}","backdrop_path":"/backdrop.jpg"}""";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"hispania-tmdb-e2e-{Guid.NewGuid():N}.sqlite");
    private static async Task<ServiceProvider> CreateServicesAsync(string path, WorkId? work) { var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider(); var catalog = services.GetRequiredService<EfLibraryCatalogStore>(); await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(catalog, default); if (work is not null) { var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(); await using var db = await factory.CreateDbContextAsync(); db.Works.Add(new WorkRecord { Id = work.Value, CategoryCode = "movie" }); await db.SaveChangesAsync(); } return services; }
    private sealed class TmdbHandler(string? detailsPayload) : HttpMessageHandler
    {
        public string? DetailsPayload { get; set; } = detailsPayload; public HttpStatusCode DetailsStatus { get; set; } = HttpStatusCode.OK; public bool CancelDetails { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { if (request.RequestUri!.AbsolutePath.EndsWith("/search/movie", StringComparison.Ordinal)) return Task.FromResult(Json(Search)); if (CancelDetails) cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(DetailsStatus == HttpStatusCode.OK ? Json(DetailsPayload!) : new HttpResponseMessage(DetailsStatus)); }
    }
}
