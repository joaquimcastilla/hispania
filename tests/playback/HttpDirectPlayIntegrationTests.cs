using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class HttpDirectPlayIntegrationTests
{
    [Fact]
    public async Task CatalogAndFullStreamUseTheRealPlannerExecutorAndLocalAccessor()
    {
        await using var host = await Host.CreateAsync([1, 2, 3, 4, 5]);
        var list = await host.Client.GetStringAsync("/api/v1/media");
        Assert.Contains(host.Entry.Id.Value.ToString(), list, StringComparison.Ordinal);
        Assert.DoesNotContain("rootKey", list, StringComparison.OrdinalIgnoreCase);

        var response = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/stream");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(5, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal([1, 2, 3, 4, 5], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RangeStreamsOnlyTheRequestedBytesAndMalformedRangesAreBadRequest()
    {
        await using var host = await Host.CreateAsync(Enumerable.Range(0, 200).Select(value => (byte)value).ToArray());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{host.Entry.Id.Value}/stream");
        request.Headers.TryAddWithoutValidation("Range", "bytes=100-199");
        var response = await host.Client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100-199/200", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Equal(Enumerable.Range(100, 100).Select(value => (byte)value), await response.Content.ReadAsByteArrayAsync());

        using var malformed = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{host.Entry.Id.Value}/stream");
        malformed.Headers.TryAddWithoutValidation("Range", "items=1-2");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await host.Client.SendAsync(malformed)).StatusCode);
    }

    [Fact]
    public async Task OpenEndedAndSuffixRangesAreSupportedWhileMultipleRangesAreRejected()
    {
        await using var host = await Host.CreateAsync([0, 1, 2, 3, 4]);
        var url = $"/api/v1/media/{host.Entry.Id.Value}/stream";

        using var openEnded = new HttpRequestMessage(HttpMethod.Get, url);
        openEnded.Headers.TryAddWithoutValidation("Range", "bytes=2-");
        using var openEndedResponse = await host.Client.SendAsync(openEnded);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, openEndedResponse.StatusCode);
        Assert.Equal([2, 3, 4], await openEndedResponse.Content.ReadAsByteArrayAsync());

        using var suffix = new HttpRequestMessage(HttpMethod.Get, url);
        suffix.Headers.TryAddWithoutValidation("Range", "bytes=-2");
        using var suffixResponse = await host.Client.SendAsync(suffix);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, suffixResponse.StatusCode);
        Assert.Equal([3, 4], await suffixResponse.Content.ReadAsByteArrayAsync());

        using var multiple = new HttpRequestMessage(HttpMethod.Get, url);
        multiple.Headers.TryAddWithoutValidation("Range", "bytes=0-1,3-4");
        Assert.Equal(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable, (await host.Client.SendAsync(multiple)).StatusCode);
    }

    [Fact]
    public async Task RestartedSqliteCatalogServesTheSameMediaAndRange()
    {
        var bytes = Enumerable.Range(0, 200).Select(value => (byte)value).ToArray();
        await using var host = await Host.CreatePersistedAsync(bytes);

        var list = await host.Client.GetStringAsync("/api/v1/media");
        Assert.Contains(host.Entry.Id.Value.ToString(), list, StringComparison.Ordinal);

        using var full = await host.Client.GetAsync($"/api/v1/media/{host.Entry.Id.Value}/stream");
        Assert.Equal(System.Net.HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("video/mp4", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bytes", full.Headers.AcceptRanges.Single());
        Assert.Equal(bytes, await full.Content.ReadAsByteArrayAsync());

        using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{host.Entry.Id.Value}/stream");
        range.Headers.TryAddWithoutValidation("Range", "bytes=100-199");
        using var partial = await host.Client.SendAsync(range);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(100, partial.Content.Headers.ContentLength);
        Assert.Equal("bytes 100-199/200", partial.Content.Headers.ContentRange!.ToString());
        Assert.Equal(bytes[100..200], await partial.Content.ReadAsByteArrayAsync());
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _directory;
        private Host(WebApplication application, HttpClient client, MediaCatalogEntry entry, string directory) { _application = application; Client = client; Entry = entry; _directory = directory; }
        public HttpClient Client { get; }
        public MediaCatalogEntry Entry { get; }

        public static async Task<Host> CreateAsync(byte[] bytes)
        {
            var directory = Path.Combine(Path.GetTempPath(), "hispania-http-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "film.mp4"), bytes);
            var info = new FileInfo(Path.Combine(directory, "film.mp4"));
            var entry = DirectPlayPlannerTests.Entry(bytes: bytes, snapshot: new LibrarySourceSnapshot(bytes.Length, info.LastWriteTimeUtc));
            var catalog = new InMemoryMediaCatalog(); catalog.Upsert(entry);
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IMediaCatalogReader>(catalog);
            builder.Services.AddSingleton<IPlaybackPlanner, DirectPlayPlanner>();
            builder.Services.AddSingleton<ISourceContentAccessor>(new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory)])));
            builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
            var app = builder.Build(); app.MapDirectPlayEndpoints(); await app.StartAsync();
            return new Host(app, app.GetTestClient(), entry, directory);
        }

        public static async Task<Host> CreatePersistedAsync(byte[] bytes)
        {
            var directory = Path.Combine(Path.GetTempPath(), "hispania-http-persistence-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, "film.mp4");
            await File.WriteAllBytesAsync(filePath, bytes);
            var snapshot = new LibrarySourceSnapshot(bytes.Length, new FileInfo(filePath).LastWriteTimeUtc);
            var scope = new LibraryScanScope(["movies"]);
            var key = new ImportKey("movies", "film.mp4");
            var identity = new ImportedPlayableMovieIdentityFactory().Create();
            var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(
                new DiscoveredMediaCandidate(new LibraryFileReference("movies", "film.mp4"), bytes.Length, snapshot.LastModifiedUtc),
                new ProbeSucceeded(new ProbeResult("mp4", TimeSpan.FromSeconds(2), [new VideoStreamObservation(0, "h264", 1920, 1080, new ObservedRational(24, 1), 8, null), new AudioStreamObservation(1, "aac", 48000, 2, null, null)])),
                identity)).Entry;
            var database = Path.Combine(directory, "catalog.sqlite");
            using (var initial = new ServiceCollection().AddSqliteLibraryPersistence(database).BuildServiceProvider())
            {
                var store = initial.GetRequiredService<EfLibraryCatalogStore>();
                await initial.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
                await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, [new MappedImport(key, snapshot, identity, entry)]));
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSqliteLibraryPersistence(database);
            builder.Services.AddSingleton(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory)]));
            builder.Services.AddSingleton<IPlaybackPlanner, DirectPlayPlanner>();
            builder.Services.AddSingleton<ISourceContentAccessor, LocalFileSystemSourceContentAccessor>();
            builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
            var app = builder.Build();
            var restartedStore = app.Services.GetRequiredService<EfLibraryCatalogStore>();
            await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(restartedStore, default);
            app.MapDirectPlayEndpoints();
            await app.StartAsync();
            return new Host(app, app.GetTestClient(), entry, directory);
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await _application.StopAsync(); await _application.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    }
}
