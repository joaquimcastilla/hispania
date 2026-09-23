using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class TmdbMovieIdentificationHttpIntegrationTests
{
    [Fact]
    public async Task MetadataEditorEndpointReturnsProviderNeutralReadModelAndSupportsSettingsAndInvalidFields()
    {
        var directory = NewDirectory();
        try
        {
            await using var host = await Host.CreateAsync(directory, DirectPlayPlannerTests.Entry(), new TmdbHandler());
            await host.SelectAsync("123"); await host.ConfirmAsync("123"); await host.RefreshAsync();
            var mediaId = host.Entry.Id.Value;
            using var initial = await host.Client.GetAsync($"/api/v1/media/{mediaId}/metadata/edit");
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            Assert.Equal("application/json", initial.Content.Headers.ContentType!.MediaType);
            using var initialJson = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
            Assert.Equal(mediaId, initialJson.RootElement.GetProperty("mediaId").GetGuid());
            Assert.Equal(host.Entry.Media.Work.Id.Value, initialJson.RootElement.GetProperty("workId").GetGuid());
            var settings = await host.Client.PutAsJsonAsync($"/api/v1/media/{mediaId}/metadata/settings", new { preferredMetadataLanguage = "es-ES", metadataCountryRegion = "ES", lockItemMetadata = true });
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            var manual = await host.Client.PutAsJsonAsync($"/api/v1/media/{mediaId}/metadata/scalars/title/manual", new { value = "Manual title" });
            Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
            var invalid = await host.Client.PutAsJsonAsync($"/api/v1/media/{mediaId}/metadata/scalars/not-a-real-field/manual", new { value = "x" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var missing = await host.Client.GetAsync($"/api/v1/media/{Guid.NewGuid()}/metadata/edit");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MetadataEditorMutationsReturnUpdatedModelsForLocksCollectionsAndClears()
    {
        var directory = NewDirectory();
        try
        {
            await using var host = await Host.CreateAsync(directory, DirectPlayPlannerTests.Entry(), new TmdbHandler());
            await host.SelectAsync("123"); await host.ConfirmAsync("123"); await host.RefreshAsync();
            var id = host.Entry.Id.Value;
            async Task<JsonDocument> Put(string path, object body)
            { var response = await host.Client.PutAsJsonAsync($"/api/v1/media/{id}/metadata/{path}", body); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return JsonDocument.Parse(await response.Content.ReadAsStringAsync()); }
            async Task<JsonDocument> Delete(string path)
            { var response = await host.Client.DeleteAsync($"/api/v1/media/{id}/metadata/{path}"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return JsonDocument.Parse(await response.Content.ReadAsStringAsync()); }
            static JsonElement Field(JsonDocument json, string code) => Assert.Single(json.RootElement.GetProperty("fields").EnumerateArray(), x => string.Equals(x.GetProperty("fieldCode").GetString(), code, StringComparison.OrdinalIgnoreCase));
            static JsonElement Collection(JsonDocument json, string code) => Assert.Single(json.RootElement.GetProperty("collections").EnumerateArray(), x => x.GetProperty("collectionCode").GetString() == code);

            using (var locked = await Put("locks/description", new { locked = true })) Assert.True(Field(locked, "description").GetProperty("locked").GetBoolean());
            using (var unlocked = await Put("locks/description", new { locked = false })) Assert.False(Field(unlocked, "description").GetProperty("locked").GetBoolean());
            using (var genreLocked = await Put("locks/genres", new { locked = true })) Assert.True(Collection(genreLocked, "genres").GetProperty("locked").GetBoolean());
            using (var manualTitle = await Put("scalars/title/manual", new { value = "Manual title" })) Assert.Equal("Manual title", Field(manualTitle, "title").GetProperty("manual").GetString());
            using (var clearedTitle = await Delete("scalars/title/manual")) Assert.Equal(JsonValueKind.Null, Field(clearedTitle, "title").GetProperty("manual").ValueKind);
            using (var manualGenres = await Put("genres/manual", new { values = new List<string> { "Thriller" } })) Assert.Equal("Thriller", Assert.Single(Collection(manualGenres, "genres").GetProperty("manual").EnumerateArray()).GetString());
            using (var emptyGenres = await Put("genres/manual", new { values = Array.Empty<string>() }))
            { var genres = Collection(emptyGenres, "genres"); Assert.Empty(genres.GetProperty("manual").EnumerateArray()); Assert.Empty(genres.GetProperty("effective").EnumerateArray()); }
            using (var clearedGenres = await Delete("genres/manual")) Assert.Empty(Collection(clearedGenres, "genres").GetProperty("manual").EnumerateArray());
            using (var manualTags = await Put("tags/manual", new { values = new List<string> { "secret agent" } })) Assert.Equal("secret agent", Assert.Single(Collection(manualTags, "tags").GetProperty("manual").EnumerateArray()).GetString());
            using (var clearedTags = await Delete("tags/manual")) Assert.Empty(Collection(clearedTags, "tags").GetProperty("manual").EnumerateArray());
            using (var manualStudios = await Put("studios/manual", new { values = new List<string> { "Manual Studio" } })) Assert.Equal("Manual Studio", Assert.Single(Collection(manualStudios, "studios").GetProperty("manual").EnumerateArray()).GetString());
            using (var clearedStudios = await Delete("studios/manual")) Assert.Empty(Collection(clearedStudios, "studios").GetProperty("manual").EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.DeleteAsync($"/api/v1/media/{id}/metadata/scalars/not-a-real-field/manual")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync($"/api/v1/media/{id}/metadata/locks/not-a-real-field", new { locked = true })).StatusCode);
            var missing = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync($"/api/v1/media/{missing}/metadata/settings", new { lockItemMetadata = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/v1/media/{missing}/metadata/genres/manual")).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MetadataEditorHttpPreservesMultiProviderSourcesPeopleAndCredits()
    {
        var directory = NewDirectory();
        try
        {
            await using var host = await Host.CreateAsync(directory, DirectPlayPlannerTests.Entry(), new TmdbHandler());
            var work = host.Entry.Media.Work.Id;
            var target = EnrichmentTarget.For(work);
            var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
            var later = DateTimeOffset.UtcNow.AddMinutes(-1);
            await host.RichStore.ApplyAsync(new RichMetadataWrite(target, new NormalizedMovieRichMetadata(["Action"], ["spy"], [new("s1", "Studio A")], [new("same", "Person A", ProviderCode: "tmdb")], [new("same", "Person A", "cast", "Hero", null, 0)]), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, earlier, new ExternalIdentifierProvider("tmdb"))));
            await host.RichStore.ApplyAsync(new RichMetadataWrite(target, new NormalizedMovieRichMetadata(["Drama"], ["espionage"], [new("s2", "Studio B")], [new("same", "Person A", ProviderCode: "other-provider")], [new("same", "Person A", "director", null, "Director", 1)]), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, later, new ExternalIdentifierProvider("other-provider"))));
            await host.Store.ApplyAsync(new MetadataEnrichmentWrite([], [new(MetadataAssertionId.New(), target, new MetadataField("title"), MetadataValue.Text("TMDB title"), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, earlier, new ExternalIdentifierProvider("tmdb")), MetadataAssertionStatus.Active), new(MetadataAssertionId.New(), target, new MetadataField("title"), MetadataValue.Text("Other title"), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, later, new ExternalIdentifierProvider("other-provider")), MetadataAssertionStatus.Active)], [], null));
            using var response = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/metadata/edit");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var title = Assert.Single(root.GetProperty("fields").EnumerateArray(), x => x.GetProperty("fieldCode").GetString() == "title");
            Assert.Equal(2, title.GetProperty("sources").GetArrayLength());
            Assert.Contains(title.GetProperty("sources").EnumerateArray(), x => x.GetProperty("providerCode").GetString() == "tmdb");
            Assert.Contains(title.GetProperty("sources").EnumerateArray(), x => x.GetProperty("providerCode").GetString() == "other-provider");
            var people = root.GetProperty("people").EnumerateArray().ToArray();
            Assert.Equal(2, people.Length);
            Assert.Equal(2, people.Select(x => x.GetProperty("internalPersonId").GetGuid()).Distinct().Count());
            var credits = root.GetProperty("credits").EnumerateArray().ToArray();
            Assert.Contains(credits, x => x.GetProperty("providerCode").GetString() == "tmdb" && x.GetProperty("creditType").GetString() == "cast");
            Assert.Contains(credits, x => x.GetProperty("providerCode").GetString() == "other-provider" && x.GetProperty("creditType").GetString() == "director");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MetadataEditorSettingsLocksAndManualOverridesSurviveHttpRestart()
    {
        var directory = NewDirectory();
        var entry = DirectPlayPlannerTests.Entry();
        try
        {
            await using (var first = await Host.CreateAsync(directory, entry, new TmdbHandler()))
            {
                await first.SelectAsync("123"); await first.ConfirmAsync("123"); await first.RefreshAsync();
                var id = entry.Id.Value;
                async Task<HttpResponseMessage> Put(string path, object body) => await first.Client.PutAsJsonAsync($"/api/v1/media/{id}/metadata/{path}", body);
                Assert.Equal(HttpStatusCode.OK, (await Put("settings", new { preferredMetadataLanguage = "es-ES", metadataCountryRegion = "ES", lockItemMetadata = true })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Put("locks/description", new { locked = true })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Put("locks/genres", new { locked = true })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Put("scalars/title/manual", new { value = "Manual Restart Title" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Put("genres/manual", new { values = new List<string> { "Thriller" } })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Put("tags/manual", new { values = new List<string> { "secret agent" } })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await Put("studios/manual", new { values = new List<string> { "Manual Studio" } })).StatusCode);
                using var before = JsonDocument.Parse(await first.Client.GetStringAsync($"/api/v1/media/{id}/metadata/edit"));
                Assert.True(before.RootElement.GetProperty("settings").GetProperty("lockItemMetadata").GetBoolean());
                Assert.Equal("Manual Restart Title", Assert.Single(before.RootElement.GetProperty("fields").EnumerateArray(), x => x.GetProperty("fieldCode").GetString() == "title").GetProperty("manual").GetString());
            }

            await using (var second = await Host.CreateAsync(directory, entry, new TmdbHandler()))
            {
                var id = entry.Id.Value;
                using var after = JsonDocument.Parse(await second.Client.GetStringAsync($"/api/v1/media/{id}/metadata/edit"));
                var root = after.RootElement;
                Assert.Equal("es-ES", root.GetProperty("settings").GetProperty("preferredMetadataLanguage").GetString());
                Assert.Equal("ES", root.GetProperty("settings").GetProperty("metadataCountryRegion").GetString());
                Assert.True(root.GetProperty("settings").GetProperty("lockItemMetadata").GetBoolean());
                Assert.True(Assert.Single(root.GetProperty("fields").EnumerateArray(), x => x.GetProperty("fieldCode").GetString() == "description").GetProperty("locked").GetBoolean());
                Assert.True(Assert.Single(root.GetProperty("collections").EnumerateArray(), x => x.GetProperty("collectionCode").GetString() == "genres").GetProperty("locked").GetBoolean());
                Assert.Equal("Manual Restart Title", Assert.Single(root.GetProperty("fields").EnumerateArray(), x => x.GetProperty("fieldCode").GetString() == "title").GetProperty("manual").GetString());
                Assert.Equal("Thriller", Assert.Single(Assert.Single(root.GetProperty("collections").EnumerateArray(), x => x.GetProperty("collectionCode").GetString() == "genres").GetProperty("manual").EnumerateArray()).GetString());
                Assert.Equal("secret agent", Assert.Single(Assert.Single(root.GetProperty("collections").EnumerateArray(), x => x.GetProperty("collectionCode").GetString() == "tags").GetProperty("manual").EnumerateArray()).GetString());
                Assert.Equal("Manual Studio", Assert.Single(Assert.Single(root.GetProperty("collections").EnumerateArray(), x => x.GetProperty("collectionCode").GetString() == "studios").GetProperty("manual").EnumerateArray()).GetString());
                var settings = await second.Client.PutAsJsonAsync($"/api/v1/media/{id}/metadata/settings", new { preferredMetadataLanguage = "es-ES", metadataCountryRegion = "ES", lockItemMetadata = false });
                Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
                using var changed = JsonDocument.Parse(await second.Client.GetStringAsync($"/api/v1/media/{id}/metadata/edit"));
                Assert.False(changed.RootElement.GetProperty("settings").GetProperty("lockItemMetadata").GetBoolean());
                var empty = await second.Client.PutAsJsonAsync($"/api/v1/media/{id}/metadata/genres/manual", new { values = Array.Empty<string>() });
                Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
                using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
                var genres = Assert.Single(emptyJson.RootElement.GetProperty("collections").EnumerateArray(), x => x.GetProperty("collectionCode").GetString() == "genres");
                Assert.Empty(genres.GetProperty("manual").EnumerateArray()); Assert.Empty(genres.GetProperty("effective").EnumerateArray());
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AutomaticEnrichmentCatalogsExactlyOneMovieAndDoesNotCallTmdbAfterSuccessfulRestart()
    {
        var directory = NewDirectory();
        var entry = DirectPlayPlannerTests.Entry() with { DisplayName = "Seduction and Snacks (2021).mp4" };
        var handler = new TmdbHandler();
        try
        {
            Guid mediaId = entry.Id.Value;
            Guid workId = entry.Media.Work.Id.Value;
            string firstMetadata;
            Guid posterId;
            await using (var host = await Host.CreateAsync(directory, entry, handler))
            {
                await host.AutoEnrichment.EnrichCatalogAsync();
                Assert.Equal(1, handler.SearchCalls);
                Assert.Equal(1, handler.DetailsCalls);

                var snapshot = await host.Store.LoadAsync();
                var association = Assert.Single(snapshot.Associations);
                Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, association.CurrentStatus);
                Assert.Equal("123", association.Identifier.Value);
                Assert.Equal("tmdb-movie-v2", Assert.Single(snapshot.RefreshStates).MappingVersion);
                Assert.Equal(MetadataRefreshStatus.Succeeded, Assert.Single(snapshot.RefreshStates).Status);
                Assert.NotNull(Assert.Single(snapshot.RefreshStates).PayloadFingerprint);
                Assert.Equal(4, snapshot.Assertions.Count);
                Assert.Equal(2, snapshot.ArtworkReferences.Count);

                using var list = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/media"));
                var item = Assert.Single(list.RootElement.EnumerateArray());
                Assert.Equal(mediaId, item.GetProperty("id").GetGuid());
                Assert.Equal("Seduction and Snacks", item.GetProperty("effective").GetProperty("title").GetString());
                Assert.Equal("2021-01-01", item.GetProperty("effective").GetProperty("releaseDate").GetString());
                var posterUrl = item.GetProperty("effective").GetProperty("posterUrl").GetString();
                Assert.Contains("/api/v1/artwork/", posterUrl);
                posterId = Assert.Single(snapshot.ArtworkReferences, x => x.Role.Code == "poster").Id.Value;
                Assert.Equal($"/api/v1/artwork/{posterId}?variant=poster-card", posterUrl);
                firstMetadata = await host.Client.GetStringAsync($"/api/v1/media/{mediaId}/metadata");
            }

            await using (var restarted = await Host.CreateAsync(directory, entry, handler))
            {
                await restarted.AutoEnrichment.EnrichCatalogAsync();
                Assert.Equal(1, handler.SearchCalls);
                Assert.Equal(1, handler.DetailsCalls);
                var snapshot = await restarted.Store.LoadAsync();
                Assert.Equal(workId, Assert.Single(snapshot.Associations).Target.WorkId!.Value);
                Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, Assert.Single(snapshot.Associations).CurrentStatus);
                Assert.Equal(4, snapshot.Assertions.Count);
                Assert.Equal(2, snapshot.ArtworkReferences.Count);
                Assert.Equal(firstMetadata, await restarted.Client.GetStringAsync($"/api/v1/media/{mediaId}/metadata"));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AutomaticEnrichmentLeavesAmbiguousMovieCataloguedWithoutDetailsCall()
    {
        var directory = NewDirectory();
        var handler = new TmdbHandler { AmbiguousSearch = true };
        var entry = DirectPlayPlannerTests.Entry() with { DisplayName = "Seduction and Snacks (2021).mp4" };
        try
        {
            await using var host = await Host.CreateAsync(directory, entry, handler);
            var run = await host.AutoEnrichment.EnrichCatalogAsync();
            Assert.Equal(1, handler.SearchCalls);
            Assert.Equal(0, handler.DetailsCalls);
            var issue = Assert.Single(run.ItemsWithoutMetadata);
            Assert.Equal("ambiguous", issue.ReasonCode);
            Assert.Equal("Seduction and Snacks (2021)", issue.DisplayName);
            Assert.Empty((await host.Store.LoadAsync()).Associations);
            using var list = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/media"));
            Assert.Equal("Seduction and Snacks (2021).mp4", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("displayName").GetString());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task IncrementalEnrichmentSkipsMoviesAlreadyLinkedToTheProvider()
    {
        var directory = NewDirectory();
        try
        {
            var handler = new TmdbHandler();
            var entry = DirectPlayPlannerTests.Entry() with { DisplayName = "Seduction and Snacks (2021).mp4" };
            await using var host = await Host.CreateAsync(directory, entry, handler);
            await host.SelectAsync("123");
            await host.ConfirmAsync("123");
            await host.RefreshAsync();
            Assert.Equal(1, handler.DetailsCalls);

            var run = await host.AutoEnrichment.EnrichUnidentifiedCatalogAsync();

            Assert.Equal(0, run.ProcessedItems);
            Assert.Empty(run.ItemsWithoutMetadata);
            Assert.Equal(0, handler.SearchCalls);
            Assert.Equal(1, handler.DetailsCalls);

            await host.AutoEnrichment.RefreshEntireCatalogAsync();
            Assert.Equal(2, handler.DetailsCalls);
            Assert.Equal(0, handler.SearchCalls);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MovieMetadataIndexExportsCatalogPathsAndConfirmedProviderIdentifiers()
    {
        var directory = NewDirectory();
        try
        {
            var handler = new TmdbHandler();
            var entry = DirectPlayPlannerTests.Entry() with { DisplayName = "American Beauty (1999).mkv" };
            await using var host = await Host.CreateAsync(directory, entry, handler);
            await host.SelectAsync("123");
            await host.ConfirmAsync("123");
            var path = Path.Combine(directory, "movies-index.json");

            await new MovieMetadataIndexDocument(path, host.Catalog, host.Store).WriteAsync();

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var movie = Assert.Single(document.RootElement.GetProperty("movies").EnumerateArray());
            Assert.Equal("American Beauty (1999).mkv", movie.GetProperty("displayName").GetString());
            Assert.Equal(entry.File.RelativePath, movie.GetProperty("relativePath").GetString());
            var identifier = Assert.Single(movie.GetProperty("identifiers").EnumerateArray());
            Assert.Equal("tmdb", identifier.GetProperty("provider").GetString());
            Assert.Equal("movie", identifier.GetProperty("namespace").GetString());
            Assert.Equal("123", identifier.GetProperty("value").GetString());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SearchSelectConfirmRefreshAndArtworkDeliveryUseOnlyHispaniaContractsAndSurviveRestart()
    {
        var directory = NewDirectory();
        try
        {
            var handler = new TmdbHandler();
            var entry = DirectPlayPlannerTests.Entry();
            Guid associationId, posterId, backdropId;
            string posterUrl, backdropUrl, effectiveBefore;
            await using (var host = await Host.CreateAsync(directory, entry, handler))
            {
                using var search = await host.Client.GetAsync($"/api/v1/media/{entry.Id.Value}/metadata/tmdb/search?query=Seduction%20and%20Snacks&year=2021");
                Assert.Equal(HttpStatusCode.OK, search.StatusCode);
                var searchBody = await search.Content.ReadAsStringAsync();
                Assert.DoesNotContain("fake-token", searchBody, StringComparison.Ordinal);
                using (var json = JsonDocument.Parse(searchBody))
                {
                    var candidate = json.RootElement[0];
                    Assert.Equal("tmdb", candidate.GetProperty("provider").GetString());
                    Assert.Equal("movie", candidate.GetProperty("externalIdentifier").GetProperty("namespace").GetString());
                    Assert.Equal("123", candidate.GetProperty("externalIdentifier").GetProperty("value").GetString());
                    Assert.Equal("Seduction and Snacks", candidate.GetProperty("title").GetString());
                    Assert.Equal("Seduction & Snacks", candidate.GetProperty("originalTitle").GetString());
                    Assert.Equal("2021-01-01", candidate.GetProperty("releaseDate").GetString());
                }
                Assert.Empty((await host.Store.LoadAsync()).Associations);

                Assert.Equal(HttpStatusCode.OK, (await host.SelectAsync("123")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.SelectAsync("123")).StatusCode);
                var candidateAssociation = Assert.Single((await host.Store.LoadAsync()).Associations);
                Assert.Equal(ExternalIdentifierAssociationStatus.Candidate, candidateAssociation.CurrentStatus);
                associationId = candidateAssociation.Id.Value;

                Assert.Equal(HttpStatusCode.OK, (await host.ConfirmAsync("123")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.ConfirmAsync("123")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.SelectAsync("456")).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await host.ConfirmAsync("456")).StatusCode);
                var snapshot = await host.Store.LoadAsync();
                Assert.Single(snapshot.Associations, x => x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);

                Assert.Equal(HttpStatusCode.OK, (await host.RefreshAsync()).StatusCode);
                snapshot = await host.Store.LoadAsync();
                Assert.Equal(4, snapshot.Assertions.Count);
                Assert.Equal(2, snapshot.ArtworkReferences.Count);
                var refresh = Assert.Single(snapshot.RefreshStates);
                Assert.Equal(MetadataRefreshStatus.Succeeded, refresh.Status);
                Assert.Equal("tmdb-movie-v2", refresh.MappingVersion);
                Assert.False(string.IsNullOrWhiteSpace(refresh.PayloadFingerprint));
                posterId = Assert.Single(snapshot.ArtworkReferences, x => x.Role.Code == "poster").Id.Value;
                backdropId = Assert.Single(snapshot.ArtworkReferences, x => x.Role.Code == "backdrop").Id.Value;

                using var metadata = JsonDocument.Parse(await host.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata"));
                var root = metadata.RootElement;
                Assert.Equal("Seduction and Snacks", root.GetProperty("title").GetProperty("value").GetString());
                Assert.Equal("Seduction & Snacks", root.GetProperty("originalTitle").GetProperty("value").GetString());
                Assert.Equal("Overview A", root.GetProperty("description").GetProperty("value").GetString());
                Assert.Equal("2021-01-01", root.GetProperty("releaseDate").GetProperty("value").GetString());
                posterUrl = root.GetProperty("artwork").GetProperty("poster").GetProperty("deliveryUrl").GetString()!;
                backdropUrl = root.GetProperty("artwork").GetProperty("backdrop").GetProperty("deliveryUrl").GetString()!;
                Assert.Equal($"/api/v1/artwork/{posterId}?variant=poster-card", posterUrl);
                Assert.Equal($"/api/v1/artwork/{backdropId}?variant=backdrop-detail", backdropUrl);

                using var list = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/media"));
                var effective = list.RootElement[0].GetProperty("effective");
                Assert.Equal("Seduction and Snacks", effective.GetProperty("title").GetString());
                Assert.Equal("2021-01-01", effective.GetProperty("releaseDate").GetString());
                Assert.Equal(posterUrl, effective.GetProperty("posterUrl").GetString());
                Assert.DoesNotContain("tmdb-image", effective.GetProperty("posterUrl").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain("image.tmdb.org", effective.GetProperty("posterUrl").GetString(), StringComparison.Ordinal);

                using var image = await host.Client.GetAsync(posterUrl);
                Assert.Equal(HttpStatusCode.OK, image.StatusCode);
                Assert.Equal("image/jpeg", image.Content.Headers.ContentType!.MediaType);
                Assert.Equal("public, max-age=86400", image.Headers.CacheControl!.ToString());
                Assert.Equal([7, 8, 9], await image.Content.ReadAsByteArrayAsync());

                effectiveBefore = await host.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata");
                Assert.Equal(HttpStatusCode.OK, (await host.RefreshAsync()).StatusCode);
                var repeated = await host.Store.LoadAsync();
                Assert.Equal(4, repeated.Assertions.Count);
                Assert.Equal(2, repeated.ArtworkReferences.Count);
                Assert.Equal(refresh.PayloadFingerprint, Assert.Single(repeated.RefreshStates).PayloadFingerprint);
                Assert.Equal(effectiveBefore, await host.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata"));
            }

            await using var restarted = await Host.CreateAsync(directory, entry, handler);
            var after = await restarted.Store.LoadAsync();
            Assert.Equal(associationId, Assert.Single(after.Associations, x => x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed).Id.Value);
            Assert.Contains(after.ArtworkReferences, x => x.Id.Value == posterId);
            Assert.Contains(after.ArtworkReferences, x => x.Id.Value == backdropId);
            Assert.Equal(effectiveBefore, await restarted.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata"));
            using var state = JsonDocument.Parse(await restarted.Client.GetStringAsync($"/api/v1/media/{entry.Id.Value}/metadata/tmdb"));
            Assert.Equal("123", state.RootElement.GetProperty("confirmed").GetProperty("externalIdentifier").GetProperty("value").GetString());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DefaultIdentificationSearchParsesFolderTitleAndYearBeforeCallingTmdb()
    {
        var directory = NewDirectory();
        try
        {
            var handler = new TmdbHandler();
            var entry = DirectPlayPlannerTests.Entry() with { DisplayName = "American Beauty (1999).mp4" };
            await using var host = await Host.CreateAsync(directory, entry, handler);
            using var response = await host.Client.GetAsync($"/api/v1/media/{entry.Id.Value}/metadata/tmdb/search");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("query=American%20Beauty", handler.LastSearchQuery, StringComparison.Ordinal);
            Assert.Contains("primary_release_year=1999", handler.LastSearchQuery, StringComparison.Ordinal);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChangedPayloadSupersedesDescriptionAndAddsNewPosterWithoutDeletingHistory()
    {
        await using var host = await Host.CreateAsync(NewDirectory(), DirectPlayPlannerTests.Entry(), new TmdbHandler());
        await host.SelectAsync("123"); await host.ConfirmAsync("123"); await host.RefreshAsync();
        host.Handler.Overview = "Overview B"; host.Handler.PosterPath = "/poster-b.jpg";
        Assert.Equal(HttpStatusCode.OK, (await host.RefreshAsync()).StatusCode);
        var snapshot = await host.Store.LoadAsync();
        Assert.Single(snapshot.Assertions, x => x.Field.Code == "description" && x.Status == MetadataAssertionStatus.Active && x.Value == MetadataValue.Text("Overview B"));
        Assert.Single(snapshot.Assertions, x => x.Field.Code == "description" && x.Status == MetadataAssertionStatus.Superseded && x.Value == MetadataValue.Text("Overview A"));
        Assert.Contains(snapshot.ArtworkReferences, x => x.Locator.OpaqueValue == "/poster-a.jpg");
        Assert.Contains(snapshot.ArtworkReferences, x => x.Locator.OpaqueValue == "/poster-b.jpg");
        using var metadata = JsonDocument.Parse(await host.Client.GetStringAsync($"/api/v1/media/{host.Entry.Id.Value}/metadata"));
        Assert.Equal("Overview B", metadata.RootElement.GetProperty("description").GetProperty("value").GetString());
        Assert.Contains("/api/v1/artwork/", metadata.RootElement.GetProperty("artwork").GetProperty("poster").GetProperty("deliveryUrl").GetString());
        var currentPosterId = metadata.RootElement.GetProperty("artwork").GetProperty("poster").GetProperty("winnerArtworkReferenceId").GetGuid();
        Assert.Equal("/poster-b.jpg", snapshot.ArtworkReferences.Single(x => x.Id.Value == currentPosterId).Locator.OpaqueValue);
    }

    [Fact]
    public async Task MissingMediaAndProviderFailuresReturnCoherentHttpStatusesWithoutPartialEnrichment()
    {
        await using var host = await Host.CreateAsync(NewDirectory(), DirectPlayPlannerTests.Entry(), new TmdbHandler());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/v1/media/{Guid.NewGuid()}/metadata/tmdb/search")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.RefreshAsync()).StatusCode);
        await host.SelectAsync("123"); await host.ConfirmAsync("123");
        host.Handler.DetailsStatus = HttpStatusCode.InternalServerError;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.RefreshAsync()).StatusCode);
        var failed = await host.Store.LoadAsync();
        Assert.Empty(failed.Assertions); Assert.Empty(failed.ArtworkReferences); Assert.Equal(MetadataRefreshStatus.Failed, Assert.Single(failed.RefreshStates).Status);
        host.Handler.DetailsStatus = HttpStatusCode.OK; host.Handler.Timeout = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.RefreshAsync()).StatusCode);
        var timedOut = await host.Store.LoadAsync();
        Assert.Empty(timedOut.Assertions); Assert.Empty(timedOut.ArtworkReferences); Assert.Equal(MetadataRefreshStatus.Failed, Assert.Single(timedOut.RefreshStates).Status);
    }

    [Fact]
    public async Task IdentifierConflictReturnsStructured409AndKeepsHealthAndCatalogAvailable()
    {
        await using var host = await Host.CreateAsync(NewDirectory(), DirectPlayPlannerTests.Entry(), new TmdbHandler());
        var ownerWork = Hispania.Media.Domain.Identity.WorkId.New();
        await host.Application.SelectMovieMetadataCandidateAsync(ownerWork, new ExternalIdentifier(TmdbMovieMetadataProvider.Provider, TmdbMovieMetadataProvider.MovieNamespace, "123"), DateTimeOffset.UtcNow);
        var ownerAssociation = Assert.Single((await host.Store.LoadAsync()).Associations).Id;
        Assert.True((await host.Application.ConfirmMovieMetadataCandidateAsync(ownerAssociation, DateTimeOffset.UtcNow)).Confirmed);

        await host.SelectAsync("123");
        using var conflict = await host.ConfirmAsync("123");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal(ownerWork.Value, body.RootElement.GetProperty("existingWorkId").GetProperty("value").GetGuid());
        Assert.Equal(host.Entry.Media.Work.Id.Value, body.RootElement.GetProperty("attemptedWorkId").GetProperty("value").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/media")).StatusCode);
    }

    [Fact]
    public async Task AutomaticEnrichmentContinuesToNextMovieAfterAnIdentifierConflict()
    {
        var first = DirectPlayPlannerTests.Entry() with { DisplayName = "Seduction and Snacks (2021).mp4" };
        var next = DirectPlayPlannerTests.Entry() with { DisplayName = "Seduction and Snacks (2021).mp4" };
        await using var host = await Host.CreateAsync(NewDirectory(), first, new TmdbHandler());
        host.AddCatalogEntry(next);
        var owner = Hispania.Media.Domain.Identity.WorkId.New();
        await host.Application.SelectMovieMetadataCandidateAsync(owner, new ExternalIdentifier(TmdbMovieMetadataProvider.Provider, TmdbMovieMetadataProvider.MovieNamespace, "123"), DateTimeOffset.UtcNow);
        var ownerAssociation = Assert.Single((await host.Store.LoadAsync()).Associations).Id;
        await host.Application.ConfirmMovieMetadataCandidateAsync(ownerAssociation, DateTimeOffset.UtcNow);

        var run = await host.AutoEnrichment.EnrichCatalogAsync();
        Assert.Equal(2, run.ProcessedItems);
        Assert.Equal(2, run.Conflicts);
        Assert.Equal(0, run.Failures);
        Assert.Equal(2, host.Handler.SearchCalls);
        var repeatedRun = await host.AutoEnrichment.EnrichCatalogAsync();
        Assert.Equal(0, repeatedRun.Conflicts);
        Assert.Equal(2, host.Handler.SearchCalls);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/healthz")).StatusCode);
    }

    private static string NewDirectory() { var value = Path.Combine(Path.GetTempPath(), "hispania-tmdb-http", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(value); return value; }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly string directory;
        private bool disposed;
        private Host(WebApplication app, HttpClient client, IMetadataEnrichmentStore store, IRichMetadataStore richStore, MediaCatalogEntry entry, InMemoryMediaCatalog catalog, string directory, TmdbHandler handler, MovieMetadataAutoEnrichmentService autoEnrichment, MovieMetadataApplicationService application) { this.app = app; Client = client; Store = store; RichStore = richStore; Entry = entry; Catalog = catalog; this.directory = directory; Handler = handler; AutoEnrichment = autoEnrichment; Application = application; }
        public HttpClient Client { get; } public IMetadataEnrichmentStore Store { get; } public IRichMetadataStore RichStore { get; } public MediaCatalogEntry Entry { get; } public InMemoryMediaCatalog Catalog { get; } public TmdbHandler Handler { get; } public MovieMetadataAutoEnrichmentService AutoEnrichment { get; } public MovieMetadataApplicationService Application { get; }
        public static async Task<Host> CreateAsync(string directory, MediaCatalogEntry entry, TmdbHandler handler)
        {
            var catalog = new InMemoryMediaCatalog(); catalog.Upsert(entry);
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddSqliteLibraryPersistence(Path.Combine(directory, "metadata.sqlite"));
            builder.Services.AddSingleton<IMetadataEnrichmentStore>(sp => new ExistingWorkStore(sp.GetRequiredService<EfMetadataEnrichmentStore>()));
            builder.Services.AddSingleton<IMediaCatalogReader>(catalog);
            builder.Services.AddSingleton<IEffectiveMetadataResolver, EffectiveMetadataResolver>();
            builder.Services.AddSingleton<IEffectiveMetadataProjectionReader, EffectiveMetadataProjectionReader>();
            builder.Services.AddSingleton<IMetadataSettingsStore, EfMetadataSettingsStore>();
            builder.Services.AddSingleton<IRichMetadataStore, EfRichMetadataStore>();
            builder.Services.AddSingleton<IMetadataEditingService, MetadataEditingService>();
            builder.Services.AddSingleton<IMovieMetadataProvider>(Provider(handler));
            builder.Services.AddSingleton<MovieMetadataApplicationService>();
            builder.Services.AddSingleton<IMovieMetadataAutoMatcher, MovieMetadataAutoMatcher>();
            builder.Services.AddSingleton<MovieMetadataAutoEnrichmentService>();
            builder.Services.AddSingleton<IArtworkContentResolver>(new FakeArtworkResolver());
            var app = builder.Build();
            await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(app.Services.GetRequiredService<EfLibraryCatalogStore>(), default);
            app.MapGet("/healthz", () => Results.Ok()); app.MapTmdbMovieMetadataEndpoints(); app.MapEffectiveMetadataEndpoints(); app.MapMetadataEditingEndpoints(); app.MapMediaListEndpoint(); app.MapArtworkDeliveryEndpoints();
            await app.StartAsync();
            return new Host(app, app.GetTestClient(), app.Services.GetRequiredService<IMetadataEnrichmentStore>(), app.Services.GetRequiredService<IRichMetadataStore>(), entry, catalog, directory, handler, app.Services.GetRequiredService<MovieMetadataAutoEnrichmentService>(), app.Services.GetRequiredService<MovieMetadataApplicationService>());
        }
        public void AddCatalogEntry(MediaCatalogEntry entry) => Catalog.Upsert(entry);
        public Task<HttpResponseMessage> SelectAsync(string id) => PostAsync("select", id);
        public Task<HttpResponseMessage> ConfirmAsync(string id) => PostAsync("confirm", id);
        public Task<HttpResponseMessage> RefreshAsync() => Client.PostAsync($"/api/v1/media/{Entry.Id.Value}/metadata/tmdb/refresh", null);
        private Task<HttpResponseMessage> PostAsync(string action, string id) => Client.PostAsync($"/api/v1/media/{Entry.Id.Value}/metadata/tmdb/{action}", new StringContent($"{{\"tmdbMovieId\":\"{id}\"}}", Encoding.UTF8, "application/json"));
        public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private static TmdbMovieMetadataProvider Provider(TmdbHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://tmdb.invalid/3/"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fake-token");
        return new TmdbMovieMetadataProvider(client, new TmdbMovieMetadataProviderOptions { Enabled = true, ReadAccessToken = "fake-token" });
    }

    private sealed class ExistingWorkStore(EfMetadataEnrichmentStore inner) : IMetadataEnrichmentStore
    {
        public ValueTask<bool> WorkExistsAsync(Hispania.Media.Domain.Identity.WorkId workId, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<MetadataEnrichmentSnapshot> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public ValueTask<MetadataEnrichmentTargetSnapshot> LoadForTargetAsync(EnrichmentTarget target, CancellationToken cancellationToken = default) => inner.LoadForTargetAsync(target, cancellationToken);
        public ValueTask<IReadOnlyCollection<MetadataEnrichmentTargetSnapshot>> LoadForTargetsAsync(IReadOnlyCollection<EnrichmentTarget> targets, CancellationToken cancellationToken = default) => inner.LoadForTargetsAsync(targets, cancellationToken);
        public ValueTask<ArtworkReference?> GetArtworkReferenceByIdAsync(Hispania.Media.Domain.Identity.ArtworkReferenceId id, CancellationToken cancellationToken = default) => inner.GetArtworkReferenceByIdAsync(id, cancellationToken);
        public ValueTask<bool> ApplyAsync(MetadataEnrichmentWrite write, CancellationToken cancellationToken = default) => inner.ApplyAsync(write, cancellationToken);
    }

    private sealed class FakeArtworkResolver : IArtworkContentResolver
    {
        public string Scheme => "tmdb-image";
        public ValueTask<ArtworkContentResult> ResolveAsync(ArtworkLocator locator, ArtworkDeliveryVariant variant, CancellationToken cancellationToken = default) => ValueTask.FromResult<ArtworkContentResult>(new ArtworkContentResult.Success(new MemoryStream([7, 8, 9]), "image/jpeg", 3, new Lifetime()));
        private sealed class Lifetime : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class TmdbHandler : HttpMessageHandler
    {
        public HttpStatusCode DetailsStatus { get; set; } = HttpStatusCode.OK; public bool Timeout { get; set; } public string Overview { get; set; } = "Overview A"; public string PosterPath { get; set; } = "/poster-a.jpg";
        public int SearchCalls { get; private set; } public int DetailsCalls { get; private set; } public bool AmbiguousSearch { get; set; } public string? LastSearchQuery { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/movie", StringComparison.Ordinal)) { SearchCalls++; LastSearchQuery = request.RequestUri.Query.TrimStart('?'); return Task.FromResult(Json(AmbiguousSearch ? """{"page":1,"results":[{"id":123,"title":"Seduction and Snacks","original_title":"Seduction & Snacks","release_date":"2021-01-01"},{"id":456,"title":"Seduction and Snacks","original_title":"Seduction and Snacks","release_date":"2021-01-01"}]}""" : """{"page":1,"results":[{"id":123,"title":"Seduction and Snacks","original_title":"Seduction & Snacks","release_date":"2021-01-01"},{"id":456,"title":"Other","original_title":"Other","release_date":"2022-01-01"}]}""")); }
            if (Timeout) throw new OperationCanceledException("fake TMDB timeout");
            DetailsCalls++;
            if (DetailsStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(DetailsStatus));
            return Task.FromResult(Json($$"""{"id":123,"title":"Seduction and Snacks","original_title":"Seduction & Snacks","overview":"{{Overview}}","release_date":"2021-01-01","poster_path":"{{PosterPath}}","backdrop_path":"/backdrop.jpg"}"""));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
