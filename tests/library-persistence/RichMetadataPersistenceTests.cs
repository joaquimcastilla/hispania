using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class RichMetadataPersistenceTests
{
    [Fact]
    public async Task SnapshotPreservesTmdbOtherAndManualSourcesWithOrderingAndObservedAt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-rich-sources-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New();
        try
        {
            await using (var services = await Services(path, work))
            {
                var store = services.GetRequiredService<IRichMetadataStore>();
                await store.ApplyAsync(Write(work, "tmdb", new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero), ["Action", "Drama"], ["assassin"], [new("10", "TMDB Studio")], [new("123", "Angelina Jolie")], [new("123", "Angelina Jolie", "cast", "Jane Smith", null, 1)]));
                await store.ApplyAsync(Write(work, "other-provider", new DateTimeOffset(2026, 9, 21, 20, 1, 0, TimeSpan.Zero), ["Crime"], ["spy"], [new("20", "Other Studio")], [new("123", "Other Person")], [new("123", "Other Person", "director", null, "Director", 0)]));
                await store.ApplyAsync(ManualWrite(work, new DateTimeOffset(2026, 9, 21, 20, 2, 0, TimeSpan.Zero), ["Thriller"], ["secret agent"], [new("manual-studio", "Manual Studio")], [new("manual-person", "Custom Person")], [new("manual-person", "Custom Person", "cast", "Custom Character", null, 0)]));
                var snapshot = await store.LoadAsync(EnrichmentTarget.For(work));
                Assert.Equal(3, snapshot.Sources.Count);
                var tmdb = Assert.Single(snapshot.Sources, x => x.ProviderCode == "tmdb"); var other = Assert.Single(snapshot.Sources, x => x.ProviderCode == "other-provider"); var manual = Assert.Single(snapshot.Sources, x => x.Origin == MetadataProvenanceOrigin.Manual);
                Assert.Equal(["Action", "Drama"], tmdb.Genres); Assert.Equal(["assassin"], tmdb.Tags); Assert.Equal("TMDB Studio", Assert.Single(tmdb.Studios).Name); Assert.Equal("Jane Smith", Assert.Single(tmdb.Credits).Character); Assert.Equal("123", Assert.Single(tmdb.People).ProviderId);
                Assert.Equal(["Crime"], other.Genres); Assert.Equal(["spy"], other.Tags); Assert.Equal("Other Studio", Assert.Single(other.Studios).Name); Assert.Equal("director", Assert.Single(other.Credits).CreditType);
                Assert.Equal(["Thriller"], manual.Genres); Assert.Equal(["secret agent"], manual.Tags); Assert.Equal("Manual Studio", Assert.Single(manual.Studios).Name); Assert.Equal("Custom Character", Assert.Single(manual.Credits).Character);
                Assert.Equal(new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero), tmdb.ObservedAt); Assert.Equal(new DateTimeOffset(2026, 9, 21, 20, 1, 0, TimeSpan.Zero), other.ObservedAt); Assert.Equal(new DateTimeOffset(2026, 9, 21, 20, 2, 0, TimeSpan.Zero), manual.ObservedAt);
                Assert.Equal("tmdb", Assert.Single(tmdb.People).ProviderCode); Assert.Equal("other-provider", Assert.Single(other.People).ProviderCode); // same external ID, different provider is not merged
            }
            await using var reopened = await Services(path, null);
            var after = await reopened.GetRequiredService<IRichMetadataStore>().LoadAsync(EnrichmentTarget.For(work));
            Assert.Equal(3, after.Sources.Count); Assert.Contains(after.Sources, x => x.ProviderCode == "tmdb"); Assert.Contains(after.Sources, x => x.ProviderCode == "other-provider"); Assert.Contains(after.Sources, x => x.Origin == MetadataProvenanceOrigin.Manual);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task SameProviderReplacementAndClearProviderLeaveOtherSourcesIntact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-rich-clear-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New();
        try
        {
            await using var services = await Services(path, work); var store = services.GetRequiredService<IRichMetadataStore>();
            await store.ApplyAsync(Write(work, "tmdb", DateTimeOffset.UtcNow, ["Action"], ["assassin"], [new("10", "Studio A")], [new("1", "Person A")], [new("1", "Person A", "cast", "Hero", null, 0)]));
            await store.ApplyAsync(Write(work, "other-provider", DateTimeOffset.UtcNow, ["Crime"], ["spy"], [new("20", "Studio Other")], [new("1", "Other Person")], [new("1", "Other Person", "director", null, "Director", 0)]));
            await store.ApplyAsync(ManualWrite(work, DateTimeOffset.UtcNow, ["Thriller"], ["secret agent"], [new("manual", "Manual Studio")], [new("manual", "Manual Person")], [new("manual", "Manual Person", "cast", "Custom", null, 0)]));
            await store.ApplyAsync(Write(work, "tmdb", DateTimeOffset.UtcNow.AddMinutes(1), ["Drama"], ["spy"], [new("11", "Studio B")], [new("2", "Person B")], [new("2", "Person B", "cast", "Villain", null, 0)]));
            var replaced = await store.LoadAsync(EnrichmentTarget.For(work)); var tmdb = Assert.Single(replaced.Sources, x => x.ProviderCode == "tmdb"); Assert.Equal(["Drama"], tmdb.Genres); Assert.DoesNotContain("Action", tmdb.Genres); Assert.Equal("Studio B", Assert.Single(tmdb.Studios).Name);
            await store.ClearProviderAsync(EnrichmentTarget.For(work), "tmdb"); var cleared = await store.LoadAsync(EnrichmentTarget.For(work)); Assert.DoesNotContain(cleared.Sources, x => x.ProviderCode == "tmdb"); Assert.Contains(cleared.Sources, x => x.ProviderCode == "other-provider"); Assert.Contains(cleared.Sources, x => x.Origin == MetadataProvenanceOrigin.Manual);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ManualCollectionClearsAreGranularAndKeepProvidersAndCredits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-rich-granular-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New();
        try
        {
            await using var services = await Services(path, work); var store = services.GetRequiredService<IRichMetadataStore>(); var target = EnrichmentTarget.For(work);
            await store.ApplyAsync(Write(work, "tmdb", DateTimeOffset.UtcNow, ["Action"], ["assassin"], [new("10", "Studio A")], [new("1", "Person")], [new("1", "Person", "cast", "Hero", null, 0)]));
            await store.ApplyAsync(Write(work, "other", DateTimeOffset.UtcNow.AddMinutes(1), ["Crime"], ["spy"], [new("20", "Studio B")], [new("2", "Other Person")], [new("2", "Other Person", "director", null, "Director", 0)]));
            await store.SetManualCollectionAsync(target, RichMetadataCollectionType.Genres, ["Thriller"], DateTimeOffset.UtcNow);
            await store.SetManualCollectionAsync(target, RichMetadataCollectionType.Tags, ["secret agent"], DateTimeOffset.UtcNow);
            await store.SetManualCollectionAsync(target, RichMetadataCollectionType.Studios, ["Manual Studio"], DateTimeOffset.UtcNow);
            await store.ClearManualCollectionAsync(target, RichMetadataCollectionType.Genres);
            var afterGenres = await store.LoadAsync(target); var manual = Assert.Single(afterGenres.Sources, x => x.Origin == MetadataProvenanceOrigin.Manual);
            Assert.Empty(manual.Genres); Assert.Equal(["secret agent"], manual.Tags); Assert.Equal("Manual Studio", Assert.Single(manual.Studios).Name); Assert.Equal(2, afterGenres.Credits.Count); Assert.Contains(afterGenres.Sources, x => x.ProviderCode == "tmdb"); Assert.Contains(afterGenres.Sources, x => x.ProviderCode == "other");
            await store.ClearManualCollectionAsync(target, RichMetadataCollectionType.Tags); await store.ClearManualCollectionAsync(target, RichMetadataCollectionType.Studios);
            var cleared = await store.LoadAsync(target); Assert.DoesNotContain(cleared.Sources, x => x.Origin == MetadataProvenanceOrigin.Manual); Assert.Contains(cleared.Sources, x => x.ProviderCode == "tmdb"); Assert.Contains(cleared.Sources, x => x.ProviderCode == "other");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RichMetadataRestartPreservesWorkPersonSettingsLocksAndLocalFilmography()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-rich-restart-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New(); Guid personId;
        try
        {
            await using (var services = await Services(path, work))
            {
                var rich = services.GetRequiredService<IRichMetadataStore>(); var target = EnrichmentTarget.For(work);
                await rich.ApplyAsync(Write(work, "tmdb", DateTimeOffset.UtcNow, ["Action"], ["assassin"], [new("10", "Studio A")], [new("123", "Person")], [new("123", "Person", "cast", "Hero", null, 0)]));
                await rich.ApplyAsync(Write(work, "other", DateTimeOffset.UtcNow.AddMinutes(1), ["Crime"], [], [], [], []));
                await rich.SetManualCollectionAsync(target, RichMetadataCollectionType.Genres, ["Thriller"], DateTimeOffset.UtcNow.AddMinutes(2));
                await rich.SetManualCollectionAsync(target, RichMetadataCollectionType.Tags, ["secret agent"], DateTimeOffset.UtcNow.AddMinutes(2));
                await rich.SetManualCollectionAsync(target, RichMetadataCollectionType.Studios, ["Manual Studio"], DateTimeOffset.UtcNow.AddMinutes(2));
                var person = Assert.Single((await rich.LoadAsync(target)).People); personId = person.InternalPersonId!.Value;
                await rich.SetPersonProfileAsync(personId, new NormalizedPerson("123", "Person", "/person.jpg", "tmdb", personId, "Biografía almacenada", new DateOnly(1992, 11, 4), null, "Toronto, Canada"), DateTimeOffset.UtcNow);
                var settings = services.GetRequiredService<IMetadataSettingsStore>(); await settings.SetSettingsAsync(new MetadataSettings(work, "es-ES", "ES", false)); await settings.SetLockAsync(new MetadataFieldLock(work, "description", true)); await settings.SetLockAsync(new MetadataFieldLock(work, "genre", true));
                var editing = new MetadataEditingService(services.GetRequiredService<IMetadataEnrichmentStore>(), settings, rich); await editing.SetManualScalarAsync(work, "title", MetadataValue.Text("Manual Title"), DateTimeOffset.UtcNow.AddMinutes(2));
            }
            await using var reopened = await Services(path, null); var reopenedRich = reopened.GetRequiredService<IRichMetadataStore>(); var snapshot = await reopenedRich.LoadAsync(EnrichmentTarget.For(work)); var reopenedPerson = Assert.Single(snapshot.People);
            Assert.Equal(personId, reopenedPerson.InternalPersonId); Assert.Contains(snapshot.Sources, x => x.ProviderCode == "tmdb"); Assert.Contains(snapshot.Sources, x => x.ProviderCode == "other"); var manual = Assert.Single(snapshot.Sources, x => x.Origin == MetadataProvenanceOrigin.Manual); Assert.Equal(["Thriller"], manual.Genres); Assert.Equal(["secret agent"], manual.Tags); Assert.Equal("Manual Studio", Assert.Single(manual.Studios).Name);
            var settingsAfter = reopened.GetRequiredService<IMetadataSettingsStore>(); var settingsSnapshot = await settingsAfter.GetSettingsAsync(work); Assert.Equal("es-ES", settingsSnapshot.PreferredMetadataLanguage); Assert.Equal("ES", settingsSnapshot.MetadataCountryRegion); Assert.Contains(await settingsAfter.GetLocksAsync(work), x => x.FieldCode == "description" && x.IsLocked); Assert.Equal("Manual Title", (await reopened.GetRequiredService<IMetadataEnrichmentStore>().LoadForTargetAsync(EnrichmentTarget.For(work))).Assertions.Single(x => x.Field.Code == "title" && x.Provenance.Origin == MetadataProvenanceOrigin.Manual).Value.TextValue);
            var credits = await reopenedRich.GetCreditsForPersonAsync(personId); var credit = Assert.Single(credits); Assert.Equal(work, credit.WorkId); Assert.Equal(MetadataProvenanceOrigin.ExternalProvider, credit.Origin);
            var personProfile = await reopenedRich.GetPersonAsync(personId); Assert.NotNull(personProfile); Assert.Equal("Biografía almacenada", personProfile!.Person.Biography); Assert.Equal(new DateOnly(1992, 11, 4), personProfile.Person.Birthday); Assert.Equal("Toronto, Canada", personProfile.Person.PlaceOfBirth); Assert.NotNull(personProfile.ProfileObservedAt);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task EditorReadModelExposesMultiProviderSourcesAndCreditProvenance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-editor-model-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New();
        try
        {
            await using var services = await Services(path, work); var rich = services.GetRequiredService<IRichMetadataStore>(); var target = EnrichmentTarget.For(work);
            await rich.ApplyAsync(Write(work, "tmdb", DateTimeOffset.UtcNow, ["Action"], ["assassin"], [new("10", "Studio A")], [new("123", "Same Name")], [new("123", "Same Name", "cast", "Hero", null, 0)]));
            await rich.ApplyAsync(Write(work, "other", DateTimeOffset.UtcNow.AddMinutes(1), ["Crime"], ["spy"], [new("20", "Studio B")], [new("123", "Same Name")], [new("123", "Same Name", "director", null, "Director", 1)]));
            await rich.SetManualCollectionAsync(target, RichMetadataCollectionType.Genres, ["Thriller"], DateTimeOffset.UtcNow.AddMinutes(2));
            var settings = services.GetRequiredService<IMetadataSettingsStore>(); await settings.SetLockAsync(new MetadataFieldLock(work, "genre", true));
            await new MetadataEditingService(services.GetRequiredService<IMetadataEnrichmentStore>(), settings, rich).SetManualScalarAsync(work, "title", MetadataValue.Text("Manual Title"), DateTimeOffset.UtcNow.AddMinutes(2));
            var model = await new MetadataEditingService(services.GetRequiredService<IMetadataEnrichmentStore>(), settings, rich).GetAsync(work);
            Assert.Equal("Manual Title", Assert.Single(model.Fields, x => x.FieldCode == "title").ManualValue!.TextValue); Assert.Contains(Assert.Single(model.Fields, x => x.FieldCode == "title").Sources, x => x.Origin == MetadataProvenanceOrigin.Manual);
            var genres = Assert.Single(model.Collections, x => x.CollectionCode == "genres"); Assert.Equal(["Thriller"], genres.Effective); Assert.True(genres.IsLocked); Assert.Contains(genres.Sources, x => x.ProviderCode == "tmdb");
            Assert.Equal(2, model.People.Count); Assert.Equal(2, model.Credits.Count); Assert.Contains(model.Credits, x => x.ProviderCode == "tmdb" && x.CreditType == "cast"); Assert.Contains(model.Credits, x => x.ProviderCode == "other" && x.CreditType == "director"); Assert.All(model.Credits, x => Assert.Equal(work, x.WorkId));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RichMetadataIsIdempotentProviderIsolatedAndSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-rich-{Guid.NewGuid():N}.sqlite");
        var work = WorkId.New();
        var tmdbAt = new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);
        try
        {
            await using (var services = await Services(path, work))
            {
                var store = services.GetRequiredService<IRichMetadataStore>();
                var tmdb = Write(work, "tmdb", tmdbAt, ["Action", "Comedy"], ["assassin", "marriage"], [new("10", "Studio A")], [new("123", "Person A")], [new("123", "Person A", "cast", "Hero", null, 0), new("123", "Person A", "writer", null, "Writer", 1)]);
                await store.ApplyAsync(tmdb); await store.ApplyAsync(tmdb);
                var other = Write(work, "other", tmdbAt.AddMinutes(1), ["Foreign"], ["other-tag"], [new("20", "Other Studio")], [new("123", "Different Person")], [new("123", "Different Person", "producer", null, "Producer", 0)]);
                await store.ApplyAsync(other);
                var snapshot = await store.LoadAsync(EnrichmentTarget.For(work));
                Assert.Contains("Action", snapshot.Genres); Assert.Contains("Comedy", snapshot.Genres); Assert.Contains("Foreign", snapshot.Genres);
                Assert.Contains("assassin", snapshot.Tags); Assert.Contains("marriage", snapshot.Tags); Assert.Contains("other-tag", snapshot.Tags);
                Assert.Contains(snapshot.Studios, x => x.Name == "Studio A"); Assert.Contains(snapshot.Studios, x => x.Name == "Other Studio");
                Assert.Equal(3, snapshot.Credits.Count);
                Assert.Equal("Hero", snapshot.Credits.Single(x => x.CreditType == "cast").Character);
                Assert.Equal("Writer", snapshot.Credits.Single(x => x.CreditType == "writer").Job);
            }

            await using (var reopened = await Services(path, null))
            {
                var snapshot = await reopened.GetRequiredService<IRichMetadataStore>().LoadAsync(EnrichmentTarget.For(work));
                Assert.Contains("Action", snapshot.Genres); Assert.Contains("Comedy", snapshot.Genres); Assert.Contains("Foreign", snapshot.Genres);
                Assert.Contains("assassin", snapshot.Tags); Assert.Contains("marriage", snapshot.Tags); Assert.Contains("other-tag", snapshot.Tags);
                Assert.Contains(snapshot.Studios, x => x.Name == "Studio A"); Assert.Contains(snapshot.Studios, x => x.Name == "Other Studio");
                Assert.Equal(3, snapshot.Credits.Count);
                Assert.Equal(["other", "tmdb"], snapshot.People.Select(x => x.ProviderCode).Order()); Assert.All(snapshot.People, x => Assert.Equal("123", x.ProviderId));
            }

            await using (var changed = await Services(path, null))
            {
                var store = changed.GetRequiredService<IRichMetadataStore>();
                await store.ApplyAsync(Write(work, "tmdb", tmdbAt.AddHours(1), ["Action", "Drama"], ["assassin", "secret agent"], [new("20", "Studio B")], [new("123", "Person A"), new("456", "Person C")], [new("123", "Person A", "cast", "Hero Updated", null, 0), new("456", "Person C", "director", null, "Director", 1)]));
                var snapshot = await store.LoadAsync(EnrichmentTarget.For(work));
                Assert.Contains("Action", snapshot.Genres); Assert.Contains("Drama", snapshot.Genres); Assert.Contains("Foreign", snapshot.Genres); Assert.DoesNotContain("Comedy", snapshot.Genres);
                Assert.Contains("assassin", snapshot.Tags); Assert.Contains("secret agent", snapshot.Tags); Assert.Contains("other-tag", snapshot.Tags); Assert.DoesNotContain("marriage", snapshot.Tags);
                Assert.Contains(snapshot.Studios, x => x.Name == "Studio B"); Assert.Contains(snapshot.Studios, x => x.Name == "Other Studio"); Assert.Contains(snapshot.Studios, x => x.Name != "Studio A");
                Assert.Equal([("other", "123"), ("tmdb", "123"), ("tmdb", "456")], snapshot.People.Select(x => (x.ProviderCode!, x.ProviderId)).Order());
                Assert.Equal("Hero Updated", Assert.Single(snapshot.Credits, x => x.CreditType == "cast").Character);
                Assert.DoesNotContain(snapshot.Credits, x => x.PersonName == "Person A" && x.CreditType == "writer");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    private static RichMetadataWrite Write(WorkId work, string provider, DateTimeOffset at, IReadOnlyCollection<string> genres, IReadOnlyCollection<string> tags, IReadOnlyCollection<NormalizedStudio> studios, IReadOnlyCollection<NormalizedPerson> people, IReadOnlyCollection<NormalizedCredit> credits) =>
        new(EnrichmentTarget.For(work), new NormalizedMovieRichMetadata(genres, tags, studios, people, credits), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, at, new ExternalIdentifierProvider(provider), new ExternalIdentifier(new ExternalIdentifierProvider(provider), new ExternalIdentifierNamespace("movie"), "1")));
    private static RichMetadataWrite ManualWrite(WorkId work, DateTimeOffset at, IReadOnlyCollection<string> genres, IReadOnlyCollection<string> tags, IReadOnlyCollection<NormalizedStudio> studios, IReadOnlyCollection<NormalizedPerson> people, IReadOnlyCollection<NormalizedCredit> credits) =>
        new(EnrichmentTarget.For(work), new NormalizedMovieRichMetadata(genres, tags, studios, people, credits), new MetadataProvenance(MetadataProvenanceOrigin.Manual, at));

    private static async Task<ServiceProvider> Services(string path, WorkId? work)
    {
        var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var catalog = services.GetRequiredService<EfLibraryCatalogStore>();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(catalog, default);
        if (work is not null)
        {
            var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.Works.Add(new WorkRecord { Id = work.Value, CategoryCode = "movie" });
            await db.SaveChangesAsync();
        }
        return services;
    }
}
