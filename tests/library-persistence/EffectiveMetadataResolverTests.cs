using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Persistence.EFCore;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class EffectiveMetadataResolverTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddMinutes(1);

    [Fact]
    public void FieldsApplyEligibilityEditorialPrecedenceAndNoFallbacks()
    {
        var target = EnrichmentTarget.For(WorkId.New());
        var external = Assertion("00000000-0000-0000-0000-000000000010", target, "title", MetadataValue.Text("External"), External("z-provider", "2", Later, .99m));
        var local = Assertion("00000000-0000-0000-0000-000000000020", target, "title", MetadataValue.Text("Local"), Local(Earlier, .01m));
        var manualOld = Assertion("00000000-0000-0000-0000-000000000030", target, "title", MetadataValue.Text("Manual old"), Manual(Earlier));
        var manualNew = Assertion("00000000-0000-0000-0000-000000000040", target, "title", MetadataValue.Text("Manual new"), Manual(Later));
        var wrongDate = Assertion("00000000-0000-0000-0000-000000000050", target, "release-date", MetadataValue.Text("2024-01-01"), Manual(Later));
        var date = Assertion("00000000-0000-0000-0000-000000000060", target, "release-date", MetadataValue.Date(new DateOnly(2024, 1, 1)), External("a-provider", "1", Earlier));
        var retracted = Assertion("00000000-0000-0000-0000-000000000070", target, "description", MetadataValue.Text("No"), Manual(Later), MetadataAssertionStatus.Retracted);
        var invalid = Assertion("00000000-0000-0000-0000-000000000080", target, "description", MetadataValue.Text("No"), Manual(Later), MetadataAssertionStatus.Invalid);
        var superseded = Assertion("00000000-0000-0000-0000-000000000090", target, "description", MetadataValue.Text("No"), Manual(Later), MetadataAssertionStatus.Superseded);
        var projection = new EffectiveMetadataResolver().Resolve(target, [external, local, manualOld, manualNew, wrongDate, date, retracted, invalid, superseded], []);

        Assert.Equal(EffectiveMetadataResolver.PolicyVersion, projection.PolicyVersion);
        Assert.Equal("Manual new", projection.Title.Value);
        Assert.Equal(manualNew.Id, projection.Title.WinnerAssertionId);
        Assert.Equal(4, projection.Title.EligibleCandidateCount);
        Assert.Equal(new DateOnly(2024, 1, 1), projection.ReleaseDate.Value);
        Assert.Equal(date.Id, projection.ReleaseDate.WinnerAssertionId);
        Assert.Null(projection.OriginalTitle.Value);
        Assert.Equal(EffectiveMetadataResolutionReason.NoEligibleAssertion, projection.OriginalTitle.ResolutionReason);
        Assert.Null(projection.Description.Value);
        Assert.Equal(EffectiveMetadataResolutionReason.NoEligibleAssertion, projection.Description.ResolutionReason);
    }

    [Fact]
    public void TieBreakIsCanonicalAndIndependentOfInputOrder()
    {
        var target = EnrichmentTarget.For(WorkId.New());
        var providerB = Assertion("00000000-0000-0000-0000-000000000030", target, "title", MetadataValue.Text("Provider B"), External("b-provider", "1", Earlier));
        var providerASecondId = Assertion("00000000-0000-0000-0000-000000000020", target, "title", MetadataValue.Text("Provider A second"), External("a-provider", "2", Earlier));
        var providerAFirstId = Assertion("00000000-0000-0000-0000-000000000010", target, "title", MetadataValue.Text("Provider A first"), External("a-provider", "2", Earlier));
        var providerAIdentifierFirst = Assertion("00000000-0000-0000-0000-000000000040", target, "title", MetadataValue.Text("Identifier first"), External("a-provider", "1", Earlier));
        var resolver = new EffectiveMetadataResolver();
        var first = resolver.Resolve(target, [providerB, providerASecondId, providerAFirstId, providerAIdentifierFirst], []);
        var reversed = resolver.Resolve(target, [providerAIdentifierFirst, providerAFirstId, providerASecondId, providerB], []);
        var shuffled = resolver.Resolve(target, [providerASecondId, providerB, providerAIdentifierFirst, providerAFirstId], []);

        Assert.Equal(providerAIdentifierFirst.Id, first.Title.WinnerAssertionId);
        Assert.Equal(first.Title, reversed.Title);
        Assert.Equal(first.Title, shuffled.Title);
    }

    [Fact]
    public void StableAssertionIdBreaksAnOtherwiseExactTie()
    {
        var target = EnrichmentTarget.For(WorkId.New());
        var laterId = Assertion("00000000-0000-0000-0000-000000000020", target, "title", MetadataValue.Text("Later ID"), External("same-provider", "same-id", Earlier));
        var earlierId = Assertion("00000000-0000-0000-0000-000000000010", target, "title", MetadataValue.Text("Earlier ID"), External("same-provider", "same-id", Earlier));

        var projection = new EffectiveMetadataResolver().Resolve(target, [laterId, earlierId], []);

        Assert.Equal(earlierId.Id, projection.Title.WinnerAssertionId);
        Assert.Equal("Earlier ID", projection.Title.Value);
    }

    [Fact]
    public void ArtworkUsesSamePolicyAndRolesRemainIndependent()
    {
        var target = EnrichmentTarget.For(WorkId.New());
        var posterExternal = Artwork("00000000-0000-0000-0000-000000000010", target, "poster", "poster-external", External("a-provider", "1", Later));
        var posterManual = Artwork("00000000-0000-0000-0000-000000000020", target, "poster", "poster-manual", Manual(Earlier));
        var backdrop = Artwork("00000000-0000-0000-0000-000000000030", target, "backdrop", "backdrop-b", External("b-provider", "1", Earlier));
        var sameRoleLaterProvider = Artwork("00000000-0000-0000-0000-000000000040", target, "backdrop", "backdrop-a", External("a-provider", "1", Earlier));
        var resolver = new EffectiveMetadataResolver();
        var projection = resolver.Resolve(target, [], [posterExternal, backdrop, posterManual, sameRoleLaterProvider]);
        var reordered = resolver.Resolve(target, [], [sameRoleLaterProvider, posterManual, backdrop, posterExternal]);

        Assert.Equal(posterManual.Id, projection.Poster.WinnerArtworkReferenceId);
        Assert.Equal(sameRoleLaterProvider.Id, projection.Backdrop.WinnerArtworkReferenceId);
        Assert.Null(projection.Logo.Locator);
        Assert.Equal(EffectiveMetadataResolutionReason.NoEligibleArtworkReference, projection.Logo.ResolutionReason);
        Assert.Equal(projection.Poster, reordered.Poster);
        Assert.Equal(projection.Backdrop, reordered.Backdrop);
    }

    [Fact]
    public async Task ProjectionReaderLoadsTargetFactsAcrossSqliteRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-effective-{Guid.NewGuid():N}.sqlite");
        var target = EnrichmentTarget.For(WorkId.New());
        var title = Assertion("00000000-0000-0000-0000-000000000010", target, "title", MetadataValue.Text("Persisted title"), External("tmdb", "42", Earlier));
        var original = Assertion("00000000-0000-0000-0000-000000000011", target, "original-title", MetadataValue.Text("Persisted original"), External("tmdb", "42", Earlier));
        var description = Assertion("00000000-0000-0000-0000-000000000012", target, "description", MetadataValue.Text("Persisted description"), External("tmdb", "42", Earlier));
        var date = Assertion("00000000-0000-0000-0000-000000000013", target, "release-date", MetadataValue.Date(new DateOnly(2024, 1, 1)), External("tmdb", "42", Earlier));
        var poster = Artwork("00000000-0000-0000-0000-000000000014", target, "poster", "poster", External("tmdb", "42", Earlier));
        var backdrop = Artwork("00000000-0000-0000-0000-000000000015", target, "backdrop", "backdrop", External("tmdb", "42", Earlier));

        EffectiveMetadataProjection before;
        await using (var services = await CreateServicesAsync(path))
        {
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            await store.ApplyAsync(new MetadataEnrichmentWrite([], [title, original, description, date], [poster, backdrop], null));
            var targeted = await store.LoadForTargetAsync(target);
            Assert.Equal(4, targeted.Assertions.Count);
            Assert.Equal(2, targeted.ArtworkReferences.Count);
            before = await new EffectiveMetadataProjectionReader(store, new EffectiveMetadataResolver()).ReadAsync(target);
        }
        await using (var services = await CreateServicesAsync(path))
        {
            var reader = new EffectiveMetadataProjectionReader(services.GetRequiredService<IMetadataEnrichmentStore>(), new EffectiveMetadataResolver());
            var after = await reader.ReadAsync(target);
            Assert.Equal(before, after);
            Assert.Equal("Persisted title", after.Title.Value);
            Assert.Equal("backdrop", after.Backdrop.Locator!.OpaqueValue);
        }
    }

    private static MetadataAssertion Assertion(string id, EnrichmentTarget target, string field, MetadataValue value, MetadataProvenance provenance, MetadataAssertionStatus status = MetadataAssertionStatus.Active) =>
        new(new MetadataAssertionId(Guid.Parse(id)), target, new MetadataField(field), value, provenance, status, status == MetadataAssertionStatus.Superseded ? new MetadataAssertionId(Guid.Parse("00000000-0000-0000-0000-000000000999")) : null);

    private static ArtworkReference Artwork(string id, EnrichmentTarget target, string role, string locator, MetadataProvenance provenance) =>
        new(new ArtworkReferenceId(Guid.Parse(id)), target, new ArtworkRole(role), new ArtworkLocator("test-art", locator), provenance);

    private static MetadataProvenance Manual(DateTimeOffset observedAt, decimal? confidence = null) => new(MetadataProvenanceOrigin.Manual, observedAt, confidence: confidence);
    private static MetadataProvenance Local(DateTimeOffset observedAt, decimal? confidence = null) => new(MetadataProvenanceOrigin.LocalProcess, observedAt, confidence: confidence);
    private static MetadataProvenance External(string provider, string id, DateTimeOffset observedAt, decimal? confidence = null)
    {
        var external = new ExternalIdentifier(new ExternalIdentifierProvider(provider), new ExternalIdentifierNamespace("movie"), id);
        return new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, observedAt, external.Provider, external, confidence);
    }

    private static async Task<ServiceProvider> CreateServicesAsync(string path)
    {
        var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(services.GetRequiredService<EfLibraryCatalogStore>(), default);
        return services;
    }
}
