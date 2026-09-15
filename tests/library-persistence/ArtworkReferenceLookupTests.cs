using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class ArtworkReferenceLookupTests
{
    [Fact]
    public async Task LookupRoundTripsArtworkReferenceExactly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-artwork-{Guid.NewGuid():N}.sqlite");
        var target = EnrichmentTarget.For(WorkId.New());
        var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("tmdb"), new ExternalIdentifierNamespace("movie"), "123");
        var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero), identifier.Provider, identifier, .7m);
        var artwork = new ArtworkReference(ArtworkReferenceId.New(), target, new ArtworkRole("poster"), new ArtworkLocator("tmdb-image", "/poster.jpg"), provenance);
        await using (var services = await CreateAsync(path))
            await services.GetRequiredService<IMetadataEnrichmentStore>().ApplyAsync(new MetadataEnrichmentWrite([], [], [artwork], null));
        await using var reopened = await CreateAsync(path);
        var actual = await reopened.GetRequiredService<IMetadataEnrichmentStore>().GetArtworkReferenceByIdAsync(artwork.Id);
        Assert.NotNull(actual); Assert.Equal(artwork.Id, actual.Id); Assert.Equal(artwork.Target, actual.Target); Assert.Equal(artwork.Role, actual.Role); Assert.Equal(artwork.Locator, actual.Locator); Assert.Equal(artwork.Provenance, actual.Provenance);
    }

    [Fact]
    public async Task LookupReturnsNullForUnknownId()
    {
        await using var services = await CreateAsync(Path.Combine(Path.GetTempPath(), $"hispania-artwork-{Guid.NewGuid():N}.sqlite"));
        Assert.Null(await services.GetRequiredService<IMetadataEnrichmentStore>().GetArtworkReferenceByIdAsync(ArtworkReferenceId.New()));
    }

    private static async Task<ServiceProvider> CreateAsync(string path)
    {
        var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(services.GetRequiredService<EfLibraryCatalogStore>(), default);
        return services;
    }
}
