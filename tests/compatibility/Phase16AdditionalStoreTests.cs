using System.Text.Json;
using System.Text.Json.Nodes;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Persistence.EFCore;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Hispania.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Hispania.Compatibility.Tests;
public sealed partial class Phase16HistoricalDataUpgradeTests
{
    [Fact]
    public async Task MediaHistoricalManualMetadataArtworkPeopleAndLocksAreReadableByCurrentServices()
    {
        using var copy=new HistoricalDataCopy("media-library.sqlite");var path=copy.PathFor("media-library.sqlite");
        await using var provider=new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        await provider.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(provider.GetRequiredService<EfLibraryCatalogStore>(),default);
        var work=new WorkId(Id(71));var metadata=provider.GetRequiredService<EfMetadataEnrichmentStore>();Assert.True(await metadata.WorkExistsAsync(work));
        var snapshot=await metadata.LoadForTargetAsync(EnrichmentTarget.For(work));Assert.Equal("Manual historical title",Assert.Single(snapshot.Assertions).Value.TextValue);Assert.Single(snapshot.ArtworkReferences);
        var settings=await provider.GetRequiredService<IMetadataSettingsStore>().GetSettingsAsync(work);Assert.True(settings.LockItemMetadata);Assert.Equal("es",settings.PreferredMetadataLanguage);
        Assert.Single(await provider.GetRequiredService<IMetadataSettingsStore>().GetLocksAsync(work));
        var rich=await provider.GetRequiredService<IRichMetadataStore>().LoadAsync(EnrichmentTarget.For(work));Assert.Single(rich.People);Assert.Single(rich.Credits);
    }
    [Fact]
    public async Task HistoricalPlaybackJsonRetainsAssignmentsAndRejectsFutureOrCorruptStateWrites()
    {
        using var copy=new HistoricalDataCopy("playback-experiment.json");var path=copy.PathFor("playback-experiment.json");
        var store=new JsonPlaybackExperimentStore(path);var snapshot=await store.LoadAsync();Assert.NotNull(snapshot);Assert.Equal(7,snapshot.Revision);
        Assert.True(store.TryGet("TEST-ONLY-historical-experiment",new string('2',64),out var variant));Assert.Equal(3,variant.Generation);
        var document=JsonNode.Parse(File.ReadAllText(path))!;document["Experiment"]!["SchemaVersion"]=2;File.WriteAllText(path,document.ToJsonString());var bytes=File.ReadAllBytes(path);
        var future=new JsonPlaybackExperimentStore(path);await Assert.ThrowsAsync<InvalidDataException>(()=>future.LoadAsync());Assert.Throws<InvalidDataException>(()=>future.Save(null));Assert.Equal(bytes,File.ReadAllBytes(path));
        File.WriteAllText(path,"{TEST ONLY corrupt JSON");bytes=File.ReadAllBytes(path);var corrupt=new JsonPlaybackExperimentStore(path);Assert.Throws<InvalidDataException>(()=>corrupt.Save(snapshot));Assert.Equal(bytes,File.ReadAllBytes(path));
    }
}
