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
    public async Task ConfirmingAnIdentifierOwnedByAnotherWorkIsPersistedAsConflictWithoutApplyingMetadata()
    {
        var path = NewPath(); var workA = WorkId.New(); var workB = WorkId.New(); var identifier = Identifier();
        try
        {
            await using (var services = await CreateServicesAsync(path, workA))
            {
                var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
                await using (var db = await factory.CreateDbContextAsync()) { db.Works.Add(new WorkRecord { Id = workB.Value, CategoryCode = "movie" }); await db.SaveChangesAsync(); }
                var store = services.GetRequiredService<IMetadataEnrichmentStore>();
                var provider = Fake(identifier, Document(identifier, "identity-a", "Correct title", "correct-poster"));
                var app = new MovieMetadataApplicationService(provider, store);
                var associationA = await ConfirmAsync(app, store, workA, identifier);
                Assert.False((await app.ConfirmMovieMetadataCandidateAsync(associationA, ObservedAt.AddMinutes(2))).Confirmed);
                Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await app.FetchAndApplyMovieMetadataAsync(associationA));
                await app.SelectMovieMetadataCandidateAsync(workB, identifier, ObservedAt.AddMinutes(3));
                var associationB = Assert.Single((await store.LoadAsync()).Associations, x => x.Target == EnrichmentTarget.For(workB));
                var result = await app.ConfirmMovieMetadataCandidateAsync(associationB.Id, ObservedAt.AddMinutes(4));
                Assert.False(result.Confirmed);
                Assert.NotNull(result.Conflict);
                Assert.Equal(workA, result.Conflict.ExistingWorkId);
                Assert.Equal(workB, result.Conflict.AttemptedWorkId);
                Assert.Equal(identifier, result.Conflict.Identifier);

                var snapshot = await store.LoadAsync();
                Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, Assert.Single(snapshot.Associations, x => x.Target == EnrichmentTarget.For(workA)).CurrentStatus);
                var rejected = Assert.Single(snapshot.Associations, x => x.Target == EnrichmentTarget.For(workB));
                Assert.Equal(ExternalIdentifierAssociationStatus.Rejected, rejected.CurrentStatus);
                Assert.Contains("existingWorkId=" + workA.Value.ToString("D"), rejected.LifecycleEntries[^1].Reason);
                var workBSnapshot = await store.LoadForTargetAsync(EnrichmentTarget.For(workB));
                Assert.Empty(workBSnapshot.Assertions);
                Assert.Empty(workBSnapshot.ArtworkReferences);
                Assert.Contains(snapshot.Assertions, x => x.Target == EnrichmentTarget.For(workA));

            }

            await using var restarted = await CreateServicesAsync(path, null);
            var restartedStore = restarted.GetRequiredService<IMetadataEnrichmentStore>();
            var afterRestart = await restartedStore.LoadAsync();
            Assert.Single(afterRestart.Associations, x => x.Target == EnrichmentTarget.For(workA) && x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
            Assert.Single(afterRestart.Associations, x => x.Target == EnrichmentTarget.For(workB) && x.CurrentStatus == ExternalIdentifierAssociationStatus.Rejected);
            var owner = afterRestart.Associations.Single(x => x.Target == EnrichmentTarget.For(workA));
            owner.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Invalidated, ObservedAt.AddMinutes(6), new MetadataProvenance(MetadataProvenanceOrigin.Manual, ObservedAt.AddMinutes(6)), "Manual identity review."));
            await restartedStore.ApplyAsync(new MetadataEnrichmentWrite([owner], [], [], null));
            var restartedApp = new MovieMetadataApplicationService(Fake(identifier, Document(identifier, "identity-a", "Correct title", "correct-poster")), restartedStore);
            await restartedApp.SelectMovieMetadataCandidateAsync(workB, identifier, ObservedAt.AddMinutes(7));
            var reopened = Assert.Single((await restartedStore.LoadAsync()).Associations, x => x.Target == EnrichmentTarget.For(workB));
            Assert.Equal(ExternalIdentifierAssociationStatus.Candidate, reopened.CurrentStatus);
            Assert.True((await restartedApp.ConfirmMovieMetadataCandidateAsync(reopened.Id, ObservedAt.AddMinutes(8))).Confirmed);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ConcurrentConfirmationAttemptsKeepTheUniqueIndexAuthoritative()
    {
        var path = NewPath(); var workA = WorkId.New(); var workB = WorkId.New(); var identifier = Identifier();
        try
        {
            await using var services = await CreateServicesAsync(path, workA);
            var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) { db.Works.Add(new WorkRecord { Id = workB.Value, CategoryCode = "movie" }); await db.SaveChangesAsync(); }
            var store = services.GetRequiredService<IMetadataEnrichmentStore>(); var app = new MovieMetadataApplicationService(Fake(identifier, Document(identifier, "race", "Title", "poster")), store);
            await app.SelectMovieMetadataCandidateAsync(workA, identifier, ObservedAt); await app.SelectMovieMetadataCandidateAsync(workB, identifier, ObservedAt);
            var candidates = (await store.LoadAsync()).Associations.ToDictionary(x => x.Target.WorkId!.Value);
            var outcomes = await Task.WhenAll(
                Task.Run(async () => await app.ConfirmMovieMetadataCandidateAsync(candidates[workA.Value].Id, ObservedAt.AddMinutes(1))),
                Task.Run(async () => await app.ConfirmMovieMetadataCandidateAsync(candidates[workB.Value].Id, ObservedAt.AddMinutes(1))));
            var final = await store.LoadAsync();
            Assert.Single(final.Associations, x => x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
            Assert.Single(final.Associations, x => x.CurrentStatus == ExternalIdentifierAssociationStatus.Rejected);
            Assert.Single(outcomes, x => x.Confirmed);
            Assert.Single(outcomes, x => x.Conflict is not null);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

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
    public async Task SecondaryIdentifierAlreadyConfirmedForAnotherWorkDoesNotBlockMetadataOrArtwork()
    {
        var path = NewPath(); var workA = WorkId.New(); var workB = WorkId.New(); var tmdb = Identifier();
        var imdb = new ExternalIdentifier(new ExternalIdentifierProvider("fake-movies"), new ExternalIdentifierNamespace("imdb"), "tt1234567");
        try
        {
            await using var services = await CreateServicesAsync(path, workA);
            var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) { db.Works.Add(new WorkRecord { Id = workB.Value, CategoryCode = "movie" }); await db.SaveChangesAsync(); }
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            var owner = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), imdb, EnrichmentTarget.For(workB), new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, ObservedAt, new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, ObservedAt, imdb.Provider, imdb)));
            await store.ApplyAsync(new MetadataEnrichmentWrite([owner], [], [], null));
            var document = Document(tmdb, "secondary", "Correct title", "correct-poster") with { SecondaryExternalIdentifiers = [imdb] };
            var app = new MovieMetadataApplicationService(Fake(tmdb, document), store);
            var associationId = await ConfirmAsync(app, store, workA, tmdb);

            Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await app.FetchAndApplyMovieMetadataAsync(associationId));

            var snapshot = await store.LoadAsync();
            Assert.Contains(snapshot.Assertions, x => x.Target == EnrichmentTarget.For(workA) && x.Field.Code == "title");
            Assert.Contains(snapshot.ArtworkReferences, x => x.Target == EnrichmentTarget.For(workA) && x.Role.Code == "poster");
            Assert.Single(snapshot.Associations, x => x.Identifier == imdb && x.CurrentStatus == ExternalIdentifierAssociationStatus.Confirmed);
            Assert.DoesNotContain(snapshot.Associations, x => x.Identifier == imdb && x.Target == EnrichmentTarget.For(workA));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
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
    public async Task MovieRefreshFetchesNewActorProfileOnceAndPersistsItAcrossRestart()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier(); Guid personId;
        try
        {
            var actor = new NormalizedPerson("142115", "Kiana Madeira", "/kiana.jpg", "fake-movies");
            var richMetadata = new NormalizedMovieRichMetadata([], [], [], [actor], [new NormalizedCredit("142115", "Kiana Madeira", "cast", null, null, 0, "/kiana.jpg")]);
            await using (var services = await CreateServicesAsync(path, workId))
            {
                var store = services.GetRequiredService<IMetadataEnrichmentStore>(); var rich = services.GetRequiredService<IRichMetadataStore>();
                var provider = Fake(identifier, Document(identifier, "people-a", "Title", "poster", rich: richMetadata));
                var people = new FakePersonMetadataProvider(new NormalizedPerson("142115", "Kiana Madeira", "/kiana.jpg", "fake-movies", null, "Actor biography", new DateOnly(1992, 11, 4), null, "Toronto, Canada"));
                var app = new MovieMetadataApplicationService(provider, store, rich, personProvider: people);
                var associationId = await ConfirmAsync(app, store, workId, identifier);
                await app.FetchAndApplyMovieMetadataAsync(associationId);
                provider.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "people-b", "Title", "poster", rich: richMetadata));
                await app.FetchAndApplyMovieMetadataAsync(associationId);
                Assert.Equal(1, people.Calls);
                var storedPerson = Assert.Single((await rich.LoadAsync(EnrichmentTarget.For(workId))).People);
                personId = storedPerson.InternalPersonId!.Value;
                var profile = await rich.GetPersonAsync(personId);
                Assert.Equal("Actor biography", profile?.Person.Biography);
                Assert.NotNull(profile?.ProfileObservedAt);
            }

            await using var restarted = await CreateServicesAsync(path, null);
            var persisted = await restarted.GetRequiredService<IRichMetadataStore>().GetPersonAsync(personId);
            Assert.Equal("Kiana Madeira", persisted?.Person.Name);
            Assert.Equal("Actor biography", persisted?.Person.Biography);
            Assert.Equal("Toronto, Canada", persisted?.Person.PlaceOfBirth);
            Assert.NotNull(persisted?.ProfileObservedAt);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    public async Task ManualScalarLifecycleKeepsProviderRefreshAndRetractsOnlyManual(string fieldCode)
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        try
        {
            await using var services = await CreateServicesAsync(path, workId);
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            var settings = services.GetRequiredService<IMetadataSettingsStore>();
            var editing = new MetadataEditingService(store, settings);
            var fake = Fake(identifier, Document(identifier, "lifecycle-a", "Title A", "poster-a"));
            var application = new MovieMetadataApplicationService(fake, store);
            var associationId = await ConfirmAsync(application, store, workId, identifier);
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            Assert.Equal(fieldCode == "title" ? "Title A" : "Description", EffectiveText(await store.LoadForTargetAsync(EnrichmentTarget.For(workId)), fieldCode));
            await editing.SetManualScalarAsync(workId, fieldCode, MetadataValue.Text("Manual B"), ObservedAt.AddMinutes(2));
            Assert.Equal("Manual B", EffectiveText(await store.LoadForTargetAsync(EnrichmentTarget.For(workId)), fieldCode));
            fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "lifecycle-b", "Title C", "poster-c"));
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            var refreshed = await store.LoadForTargetAsync(EnrichmentTarget.For(workId));
            Assert.Equal("Manual B", EffectiveText(refreshed, fieldCode));
            Assert.Contains(refreshed.Assertions, x => x.Field.Code == fieldCode && x.Provenance.Origin == MetadataProvenanceOrigin.Manual && x.Status == MetadataAssertionStatus.Active);
            Assert.Contains(refreshed.Assertions, x => x.Field.Code == fieldCode && x.Provenance.Origin == MetadataProvenanceOrigin.ExternalProvider && x.Status == MetadataAssertionStatus.Active && x.Value == MetadataValue.Text(fieldCode == "title" ? "Title C" : "Description"));
            await editing.ClearManualScalarAsync(workId, fieldCode);
            var cleared = await store.LoadForTargetAsync(EnrichmentTarget.For(workId));
            Assert.Equal(fieldCode == "title" ? "Title C" : "Description", EffectiveText(cleared, fieldCode));
            var manual = Assert.Single(cleared.Assertions, x => x.Field.Code == fieldCode && x.Provenance.Origin == MetadataProvenanceOrigin.Manual);
            Assert.Equal(MetadataAssertionStatus.Retracted, manual.Status);
            Assert.Null(manual.SupersededByAssertionId);
            Assert.Contains(cleared.Assertions, x => x.Field.Code == fieldCode && x.Provenance.Origin == MetadataProvenanceOrigin.ExternalProvider && x.Status == MetadataAssertionStatus.Active);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ItemAndFieldLocksPreserveOnlyLockedScalarValuesDuringRealRefresh()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        try
        {
            await using var services = await CreateServicesAsync(path, workId);
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            var settings = services.GetRequiredService<IMetadataSettingsStore>();
            var fake = Fake(identifier, Document(identifier, "lock-a", "Title A", "poster-a"));
            var application = new MovieMetadataApplicationService(fake, store, services.GetRequiredService<IRichMetadataStore>(), settings);
            var associationId = await ConfirmAsync(application, store, workId, identifier);
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            await settings.SetSettingsAsync(new MetadataSettings(workId, "es-ES", "ES", true));
            fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "lock-b", "Title B", "poster-b", "Description Y"));
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            var locked = await store.LoadForTargetAsync(EnrichmentTarget.For(workId));
            Assert.Equal("Title A", EffectiveText(locked, "title"));
            Assert.Single((await store.LoadAsync()).RefreshStates, x => x.PayloadFingerprint == "lock-b");
            await settings.SetSettingsAsync(new MetadataSettings(workId, "es-ES", "ES", false));
            fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "lock-c", "Title B", "poster-b", "Description Y"));
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            var unlocked = await store.LoadForTargetAsync(EnrichmentTarget.For(workId));
            Assert.Equal("Title B", EffectiveText(unlocked, "title"));
            await settings.SetLockAsync(new MetadataFieldLock(workId, "description", true));
            fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "lock-d", "Title C", "poster-c", "Description Y"));
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            var fieldLocked = await store.LoadForTargetAsync(EnrichmentTarget.For(workId));
            Assert.Equal("Title C", EffectiveText(fieldLocked, "title"));
            Assert.Equal("Description Y", EffectiveText(fieldLocked, "description"));
            await settings.SetLockAsync(new MetadataFieldLock(workId, "description", false));
            await application.FetchAndApplyMovieMetadataAsync(associationId);
            Assert.Equal("Description Y", EffectiveText(await store.LoadForTargetAsync(EnrichmentTarget.For(workId)), "description"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RealRefreshRespectsCollectionLockAndUnlocksOnNewFingerprint()
    {
        var path = NewPath(); var workId = WorkId.New(); var identifier = Identifier();
        try
        {
            await using var services = await CreateServicesAsync(path, workId); var store = services.GetRequiredService<IMetadataEnrichmentStore>(); var rich = services.GetRequiredService<IRichMetadataStore>(); var settings = services.GetRequiredService<IMetadataSettingsStore>();
            var fake = Fake(identifier, Document(identifier, "collections-a", "Title", "poster", "Description", new(["Action"], ["assassin"], [new("10", "Studio A")], [], [])));
            var app = new MovieMetadataApplicationService(fake, store, rich, settings); var associationId = await ConfirmAsync(app, store, workId, identifier); await app.FetchAndApplyMovieMetadataAsync(associationId);
            await rich.ApplyAsync(WriteRich(workId, "other", ["Crime"], [], [], [], [])); await rich.SetManualCollectionAsync(EnrichmentTarget.For(workId), RichMetadataCollectionType.Genres, ["Thriller"], DateTimeOffset.UtcNow); await settings.SetLockAsync(new MetadataFieldLock(workId, "genre", true));
            fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "collections-b", "Title", "poster", "Description", new(["Drama"], ["spy"], [new("20", "Studio B")], [], []))); await app.FetchAndApplyMovieMetadataAsync(associationId);
            var locked = await rich.LoadAsync(EnrichmentTarget.For(workId)); Assert.Equal(["Action"], Assert.Single(locked.Sources, x => x.ProviderCode == "fake-movies").Genres); Assert.Equal(["spy"], Assert.Single(locked.Sources, x => x.ProviderCode == "fake-movies").Tags); Assert.Equal("Studio B", Assert.Single(Assert.Single(locked.Sources, x => x.ProviderCode == "fake-movies").Studios).Name); Assert.Equal(["Thriller"], EffectiveRichMetadataResolver.Resolve<string>(locked.Sources, RichMetadataCollectionType.Genres)); Assert.Contains(locked.Sources, x => x.ProviderCode == "other");
            await settings.SetLockAsync(new MetadataFieldLock(workId, "genre", false)); fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "collections-c", "Title", "poster", "Description", new(["Adventure"], ["espionage"], [new("30", "Studio C")], [], []))); await app.FetchAndApplyMovieMetadataAsync(associationId); var unlocked = await rich.LoadAsync(EnrichmentTarget.For(workId)); Assert.Equal(["Adventure"], Assert.Single(unlocked.Sources, x => x.ProviderCode == "fake-movies").Genres);
            await rich.SetManualCollectionAsync(EnrichmentTarget.For(workId), RichMetadataCollectionType.Genres, ["Thriller"], DateTimeOffset.UtcNow); await settings.SetLockAsync(new MetadataFieldLock(workId, "genre", true)); fake.DocumentResult = new MovieProviderSuccess<NormalizedMovieDocument>(Document(identifier, "collections-d", "Title", "poster", "Description", new(["Drama"], ["espionage"], [new("30", "Studio C")], [], []))); await app.FetchAndApplyMovieMetadataAsync(associationId); var guarded = await rich.LoadAsync(EnrichmentTarget.For(workId)); Assert.Equal(["Adventure"], Assert.Single(guarded.Sources, x => x.ProviderCode == "fake-movies").Genres); Assert.Equal(["Thriller"], EffectiveRichMetadataResolver.Resolve<string>(guarded.Sources, RichMetadataCollectionType.Genres)); await new MetadataEditingService(store, settings, rich).ClearManualGenresAsync(workId); var cleared = await rich.LoadAsync(EnrichmentTarget.For(workId)); Assert.Equal(["Adventure"], EffectiveRichMetadataResolver.Resolve<string>(cleared.Sources, RichMetadataCollectionType.Genres));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    private static string? EffectiveText(MetadataEnrichmentTargetSnapshot snapshot, string fieldCode)
    {
        var projection = new EffectiveMetadataResolver().Resolve(snapshot.Target, snapshot.Assertions, snapshot.ArtworkReferences);
        return fieldCode == "title" ? projection.Title.Value : projection.Description.Value;
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
        var associationId = Assert.Single((await store.LoadAsync()).Associations, x => x.Target == EnrichmentTarget.For(workId) && x.Identifier == identifier).Id;
        await service.ConfirmMovieMetadataCandidateAsync(associationId, ObservedAt.AddMinutes(1));
        return associationId;
    }

    private static FakeMovieMetadataProvider Fake(ExternalIdentifier identifier, NormalizedMovieDocument document, string mappingVersion = "movie-v1") => new(identifier, document, mappingVersion);
    private static ExternalIdentifier Identifier() => new(new ExternalIdentifierProvider("fake-movies"), new ExternalIdentifierNamespace("movie"), "42");
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"hispania-metadata-{Guid.NewGuid():N}.sqlite");

    private static NormalizedMovieDocument Document(ExternalIdentifier identifier, string fingerprint, string title, string poster, string description = "Description", NormalizedMovieRichMetadata? rich = null) => new(identifier, [], [new(new MetadataField("title"), MetadataValue.Text(title), .91m), new(new MetadataField("original-title"), MetadataValue.Text("Original"), null), new(new MetadataField("description"), MetadataValue.Text(description), null), new(new MetadataField("release-date"), MetadataValue.Date(new DateOnly(2024, 1, 1)), null)], [new(new ArtworkRole("poster"), new ArtworkLocator("https", poster), .8m), new(new ArtworkRole("backdrop"), new ArtworkLocator("https", "backdrop-a"), null), new(new ArtworkRole("logo"), new ArtworkLocator("https", "logo-a"), null)], ObservedAt, "movie-v1", fingerprint, rich);
    private static RichMetadataWrite WriteRich(WorkId work, string provider, IReadOnlyCollection<string> genres, IReadOnlyCollection<string> tags, IReadOnlyCollection<NormalizedStudio> studios, IReadOnlyCollection<NormalizedPerson> people, IReadOnlyCollection<NormalizedCredit> credits) => new(EnrichmentTarget.For(work), new NormalizedMovieRichMetadata(genres, tags, studios, people, credits), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, ObservedAt.AddHours(-1), new ExternalIdentifierProvider(provider)));

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

    private sealed class FakePersonMetadataProvider(NormalizedPerson profile) : IPersonMetadataProvider
    {
        public int Calls { get; private set; }
        public ValueTask<NormalizedPerson?> GetPersonAsync(string providerPersonId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult<NormalizedPerson?>(providerPersonId == profile.ProviderId ? profile : null);
        }
    }
}
