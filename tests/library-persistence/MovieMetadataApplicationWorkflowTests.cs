using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class MovieMetadataApplicationWorkflowTests
{
    [Fact]
    public async Task SelectedConfirmedMovieDocumentRoundTripsAcrossRestart()
    {
        var path = NewPath();
        var workId = WorkId.New();
        var identifier = Identifier();
        ExternalIdentifierAssociationId associationId;

        await using (var services = await CreateServicesAsync(path, workId))
        {
            var fake = Fake(identifier, Document(identifier, "fingerprint-a", "Title A", "poster-a"));
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            var service = new MovieMetadataApplicationService(fake, store);

            var search = Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await service.SearchMovieMetadataAsync(new MovieSearchRequest("Title A", 2024, null)));
            Assert.Single(search.Value);
            Assert.Empty((await store.LoadAsync()).Associations);

            await service.SelectMovieMetadataCandidateAsync(workId, identifier, ObservedAt);
            var candidate = Assert.Single((await store.LoadAsync()).Associations);
            associationId = candidate.Id;
            Assert.Equal(ExternalIdentifierAssociationStatus.Candidate, candidate.CurrentStatus);

            await service.ConfirmMovieMetadataCandidateAsync(associationId, ObservedAt.AddMinutes(1));
            var confirmed = Assert.Single((await store.LoadAsync()).Associations);
            Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, confirmed.CurrentStatus);
            Assert.Equal(2, confirmed.LifecycleEntries.Count);

            Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await service.FetchAndApplyMovieMetadataAsync(associationId));
            var applied = await store.LoadAsync();
            Assert.Equal(4, applied.Assertions.Count);
            Assert.Equal(3, applied.ArtworkReferences.Count);
            Assert.Single(applied.RefreshStates);
            Assert.All(applied.Assertions, assertion => Assert.Equal(MetadataProvenanceOrigin.ExternalProvider, assertion.Provenance.Origin));
            Assert.All(applied.Assertions, assertion => Assert.Equal(identifier, assertion.Provenance.RelatedExternalIdentifier));
            Assert.False(await service.IsMovieMetadataRefreshNeededAsync(associationId));
        }

        await using var restarted = await CreateServicesAsync(path, null);
        var rehydrated = await restarted.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync();
        Assert.Equal(associationId, Assert.Single(rehydrated.Associations).Id);
        Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, Assert.Single(rehydrated.Associations).CurrentStatus);
        Assert.Equal(4, rehydrated.Assertions.Count);
        Assert.Equal(3, rehydrated.ArtworkReferences.Count);
        Assert.Equal("fingerprint-a", Assert.Single(rehydrated.RefreshStates).PayloadFingerprint);
    }

    [Fact]
    public async Task SameFingerprintIsAnIdempotentNoOp()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        await using var services = await CreateServicesAsync(path, workId);
        var fake = Fake(identifier, Document(identifier, "fingerprint-a", "Title A", "poster-a"));
        var store = services.GetRequiredService<IMetadataEnrichmentStore>();
        var service = new MovieMetadataApplicationService(fake, store);
        var associationId = await ConfirmAsync(service, store, workId, identifier);

        await service.FetchAndApplyMovieMetadataAsync(associationId);
        await service.FetchAndApplyMovieMetadataAsync(associationId);

        var snapshot = await store.LoadAsync();
        Assert.Equal(4, snapshot.Assertions.Count);
        Assert.Equal(3, snapshot.ArtworkReferences.Count);
        Assert.Single(snapshot.RefreshStates);
    }

    [Fact]
    public async Task ChangedFingerprintAddsOnlyDeltaAndSupersedesSingularField()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        await using var services = await CreateServicesAsync(path, workId);
        var fake = Fake(identifier, Document(identifier, "fingerprint-a", "Title A", "poster-a"));
        var store = services.GetRequiredService<IMetadataEnrichmentStore>();
        var service = new MovieMetadataApplicationService(fake, store);
        var associationId = await ConfirmAsync(service, store, workId, identifier);
        await service.FetchAndApplyMovieMetadataAsync(associationId);

        fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "fingerprint-b", "Title B", "poster-b"));
        await service.FetchAndApplyMovieMetadataAsync(associationId);

        var snapshot = await store.LoadAsync();
        var titleAssertions = snapshot.Assertions.Where(x => x.Field.Code == "title").ToArray();
        Assert.Equal(2, titleAssertions.Length);
        Assert.Single(titleAssertions, x => x.Status == MetadataAssertionStatus.Active && x.Value == MetadataValue.Text("Title B"));
        var superseded = Assert.Single(titleAssertions, x => x.Status == MetadataAssertionStatus.Superseded);
        Assert.NotNull(superseded.SupersededByAssertionId);
        Assert.Equal(5, snapshot.Assertions.Count);
        Assert.Equal(4, snapshot.ArtworkReferences.Count);
        Assert.Equal(2, snapshot.RefreshStates.Count);
    }

    [Fact]
    public async Task RefreshDecisionIsTrueWithoutSuccessOrWhenMappingVersionChanges()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        await using var services = await CreateServicesAsync(path, workId);
        var fake = Fake(identifier, Document(identifier, "fingerprint-a", "Title A", "poster-a"));
        var service = new MovieMetadataApplicationService(fake, services.GetRequiredService<IMetadataEnrichmentStore>());
        var associationId = await ConfirmAsync(service, services.GetRequiredService<IMetadataEnrichmentStore>(), workId, identifier);
        Assert.True(await service.IsMovieMetadataRefreshNeededAsync(associationId));
        await service.FetchAndApplyMovieMetadataAsync(associationId);
        Assert.False(await service.IsMovieMetadataRefreshNeededAsync(associationId));

        var remapped = new MovieMetadataApplicationService(Fake(identifier, Document(identifier, "fingerprint-v2", "Title A", "poster-a"), "movie-v2"), services.GetRequiredService<IMetadataEnrichmentStore>());
        Assert.True(await remapped.IsMovieMetadataRefreshNeededAsync(associationId));
    }

    [Fact]
    public async Task WorkValidationProviderErrorsAndCancellationPreserveTheBoundary()
    {
        var path = NewPath(); var known = WorkId.New(); var unknown = WorkId.New(); var identifier = Identifier();
        await using var services = await CreateServicesAsync(path, known);
        var fake = Fake(identifier, Document(identifier, "fingerprint-a", "Title A", "poster-a"));
        var store = services.GetRequiredService<IMetadataEnrichmentStore>();
        var service = new MovieMetadataApplicationService(fake, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectMovieMetadataCandidateAsync(unknown, identifier, ObservedAt).AsTask());

        var associationId = await ConfirmAsync(service, store, known, identifier);
        fake.DocumentResult = new MovieProviderUnavailable("offline");
        Assert.IsType<MovieProviderUnavailable>(await service.FetchAndApplyMovieMetadataAsync(associationId));
        Assert.Equal(MetadataRefreshStatus.Failed, Assert.Single((await store.LoadAsync()).RefreshStates).Status);

        fake.ThrowOnGet = true;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAndApplyMovieMetadataAsync(associationId, cancellation.Token).AsTask());
        Assert.Single((await store.LoadAsync()).RefreshStates);
    }

    [Fact]
    public async Task SearchResultsRemainEphemeralAndPreserveProviderErrorsAndCancellation()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        await using var services = await CreateServicesAsync(path, workId);
        var fake = Fake(identifier, Document(identifier, "fingerprint-a", "Title A", "poster-a"));
        var store = services.GetRequiredService<IMetadataEnrichmentStore>();
        var service = new MovieMetadataApplicationService(fake, store);

        Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await service.SearchMovieMetadataAsync(new MovieSearchRequest("Title A", null, null)));
        Assert.Empty((await store.LoadAsync()).Associations);
        fake.SearchResult = new MovieProviderTimeout("configured timeout");
        Assert.IsType<MovieProviderTimeout>(await service.SearchMovieMetadataAsync(new MovieSearchRequest("Title A", null, null)));
        fake.ThrowOnSearch = true;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchMovieMetadataAsync(new MovieSearchRequest("Title A", null, null), cancellation.Token).AsTask());
    }

    private static async Task<ExternalIdentifierAssociationId> ConfirmAsync(MovieMetadataApplicationService service, IMetadataEnrichmentStore store, WorkId workId, ExternalIdentifier identifier)
    {
        await service.SelectMovieMetadataCandidateAsync(workId, identifier, ObservedAt);
        var associationId = Assert.Single((await store.LoadAsync()).Associations).Id;
        await service.ConfirmMovieMetadataCandidateAsync(associationId, ObservedAt.AddMinutes(1));
        return associationId;
    }

    private static FakeMovieMetadataProvider Fake(ExternalIdentifier identifier, NormalizedMovieDocument document, string mappingVersion = "movie-v1") => new(identifier, document, mappingVersion);
    private static ExternalIdentifier Identifier() => new(new ExternalIdentifierProvider("fake-movies"), new ExternalIdentifierNamespace("movie"), "42");
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"hispania-metadata-{Guid.NewGuid():N}.sqlite");

    private static NormalizedMovieDocument Document(ExternalIdentifier identifier, string fingerprint, string title, string poster) => new(identifier, [], [new(new MetadataField("title"), MetadataValue.Text(title), .91m), new(new MetadataField("original-title"), MetadataValue.Text("Original"), null), new(new MetadataField("description"), MetadataValue.Text("Description"), null), new(new MetadataField("release-date"), MetadataValue.Date(new DateOnly(2024, 1, 1)), null)], [new(new ArtworkRole("poster"), new ArtworkLocator("https", poster), .8m), new(new ArtworkRole("backdrop"), new ArtworkLocator("https", "backdrop-a"), null), new(new ArtworkRole("logo"), new ArtworkLocator("https", "logo-a"), null)], ObservedAt, "movie-v1", fingerprint);

    private static async Task<ServiceProvider> CreateServicesAsync(string path, WorkId? workId)
    {
        var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var catalog = services.GetRequiredService<EfLibraryCatalogStore>();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(catalog, default);
        if (workId is not null)
        {
            var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.Works.Add(new WorkRecord { Id = workId.Value, CategoryCode = "movie" });
            await db.SaveChangesAsync();
        }
        return services;
    }

    private sealed class FakeMovieMetadataProvider : IMovieMetadataProvider
    {
        private readonly ExternalIdentifier identifier;
        public FakeMovieMetadataProvider(ExternalIdentifier identifier, NormalizedMovieDocument document, string mappingVersion)
        {
            this.identifier = identifier;
            Descriptor = new MovieMetadataProviderDescriptor(identifier.Provider, "Fake movies", [identifier.Namespace], mappingVersion);
            DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(document with { MappingVersion = mappingVersion });
            SearchResult = new MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>([new MovieSearchCandidate(identifier, "Title A", "Original", new DateOnly(2024, 1, 1), .9m, .8m, "configured fake")]);
        }
        public MovieMetadataProviderDescriptor Descriptor { get; }
        public MovieProviderResult SearchResult { get; set; }
        public MovieProviderResult DocumentResult { get; set; }
        public bool ThrowOnSearch { get; set; }
        public bool ThrowOnGet { get; set; }
        public ValueTask<MovieProviderResult> SearchAsync(MovieSearchRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSearch) cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SearchResult);
        }
        public ValueTask<MovieProviderResult> GetMovieAsync(ExternalIdentifier requested, CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet) cancellationToken.ThrowIfCancellationRequested();
            if (requested != identifier) return ValueTask.FromResult<MovieProviderResult>(new MovieProviderUnsupported("unknown identifier"));
            return ValueTask.FromResult(DocumentResult);
        }
    }
}
