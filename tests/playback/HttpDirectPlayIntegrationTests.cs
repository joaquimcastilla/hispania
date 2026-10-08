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
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class HttpDirectPlayIntegrationTests
{
    [Theory]
    [InlineData(2131)][InlineData(2132)]
    public async Task Phase21DeterministicRangesAndOpaqueIdsRejectMalformedInputs(int seed)
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await using var host = await Host.CreateAsync(bytes);
        var random = new Random(seed);
        for (var i = 0; i < 64; i++)
        {
            var first = random.Next(256); var last = random.Next(first, 256);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{host.Entry.Id.Value}/stream");
            request.Headers.TryAddWithoutValidation("Range", $"bytes={first}-{last}");
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(bytes[first..(last + 1)], await response.Content.ReadAsByteArrayAsync());
            using var malformed = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{host.Entry.Id.Value}/stream");
            malformed.Headers.TryAddWithoutValidation("Range", "bytes=" + new string('9', random.Next(20, 100)) + "-1");
            using var rejected = await host.Client.SendAsync(malformed);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var unknown = await host.Client.GetAsync($"/api/v1/media/not-a-guid-{random.Next()}/stream");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
    }

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
    public async Task CatalogAndPlaybackRequireAValidSessionAndDomainPermission()
    {
        await using var host = await Host.CreateAsync([1, 2, 3]);
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media") { Headers = { Authorization = new AuthenticationHeaderValue("None") } };
        using var anonymousResponse = await host.Client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        using var legacy = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "legacy-admin") } };
        using var legacyResponse = await host.Client.SendAsync(legacy);
        Assert.Equal(HttpStatusCode.Unauthorized, legacyResponse.StatusCode);
        using var validButWithoutPermission = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media") { Headers = { Authorization = new AuthenticationHeaderValue("Session", "valid-session-token-for-test-user") } };
        host.Authorization.AllowedPermissions.Clear();
        using var denied = await host.Client.SendAsync(validButWithoutPermission);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        host.Authorization.AllowedPermissions.Add(MediaPermissionIds.LibraryRead);
        using var allowed = await host.Client.GetAsync("/api/v1/media");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
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

    [Fact]
    [Trait("Category", "Phase15Compatibility")]
    public async Task Historical07HttpRangeFixturesMatchCurrentObservableBehavior()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Hispania.sln"))) root = root.Parent;
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root!.FullName, "compatibility", "serialization", "0.7", "http-range.json")));
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            await using var host = await Host.CreateAsync([0, 1, 2, 3, 4]);
            if (item.GetProperty("sourceChanged").GetBoolean()) await File.WriteAllBytesAsync(Path.Combine(host.DirectoryPath, "film.mp4"), new byte[] { 0, 1, 2, 3, 4, 5 });
            var route = item.GetProperty("route").GetString()!.Replace("{id}", host.Entry.Id.Value.ToString(), StringComparison.Ordinal);
            using var request = new HttpRequestMessage(new HttpMethod(item.GetProperty("method").GetString()!), route);
            if (item.GetProperty("range").GetString() is { } range) request.Headers.TryAddWithoutValidation("Range", range);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(item.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.Equal(item.GetProperty("acceptRanges").GetString(), string.Join(",", response.Headers.AcceptRanges));
            Assert.Equal(item.GetProperty("contentType").GetString(), response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(item.GetProperty("contentRange").GetString(), response.Content.Headers.ContentRange?.ToString());
            if (item.GetProperty("contentLength").ValueKind != System.Text.Json.JsonValueKind.Null)
                Assert.Equal(item.GetProperty("contentLength").GetInt64(), response.Content.Headers.ContentLength);
            Assert.Equal(Convert.FromBase64String(item.GetProperty("bodyBase64").GetString()!), await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    [Trait("Category", "Phase15Compatibility")]
    public async Task Historical07DirectPlayResultOwnsAndDisposesItsStream()
    {
        await using var host = await Host.CreateAsync([0, 1, 2, 3, 4]);
        var catalog = new InMemoryMediaCatalog(); catalog.Upsert(host.Entry);
        var planner = new DirectPlayPlanner(catalog);
        var accessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", host.DirectoryPath)]));
        var executor = new DirectPlayExecutor(catalog, accessor);
        var plan = Assert.IsType<DirectPlayPlanned>(planner.Plan(host.Entry.Id)).Plan;
        var result = Assert.IsType<FullContent>(await executor.ExecuteAsync(plan, new ByteRangeRequest.Full()));
        var stream = result.Content.Stream;
        Assert.True(stream.CanRead);
        await result.DisposeAsync();
        Assert.False(stream.CanRead);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _directory;
        private Host(WebApplication application, HttpClient client, MediaCatalogEntry entry, string directory, TestAuthorization authorization) { _application = application; Client = client; Entry = entry; _directory = directory; Authorization = authorization; }
        public string DirectoryPath => _directory;
        public HttpClient Client { get; }
        public MediaCatalogEntry Entry { get; }
        public TestAuthorization Authorization { get; }

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
            var authorization = new TestAuthorization();
            builder.Services.AddSingleton<IIdentityQueryV1, TestIdentityQuery>();
            builder.Services.AddSingleton<IAuthorizationServiceV1>(authorization);
            var app = builder.Build(); app.MapDirectPlayEndpoints(); await app.StartAsync();
            var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Session", "valid-session-token-for-test-user");
            return new Host(app, client, entry, directory, authorization);
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
            var authorization = new TestAuthorization();
            builder.Services.AddSingleton<IIdentityQueryV1, TestIdentityQuery>();
            builder.Services.AddSingleton<IAuthorizationServiceV1>(authorization);
            var app = builder.Build();
            var restartedStore = app.Services.GetRequiredService<EfLibraryCatalogStore>();
            await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(restartedStore, default);
            app.MapDirectPlayEndpoints();
            await app.StartAsync();
            var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Session", "valid-session-token-for-test-user");
            return new Host(app, client, entry, directory, authorization);
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await _application.StopAsync(); await _application.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    }

    public sealed class TestAuthorization : IAuthorizationServiceV1
    {
        public HashSet<string> AllowedPermissions { get; } = [MediaPermissionIds.LibraryRead, MediaPermissionIds.PlaybackUse];
        public ValueTask<bool> HasPermissionAsync(UserId userId, string permissionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(AllowedPermissions.Contains(permissionId));
        public ValueTask<IReadOnlySet<string>> GetEffectivePermissionsAsync(UserId userId, CancellationToken cancellationToken = default) => ValueTask.FromResult((IReadOnlySet<string>)AllowedPermissions);
        public ValueTask<AuthorizationDecision> RequirePermissionAsync(UserId? userId, string permissionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(userId is null ? new AuthorizationDecision(false, false) : new AuthorizationDecision(true, AllowedPermissions.Contains(permissionId)));
    }

    private sealed class TestIdentityQuery : IIdentityQueryV1
    {
        private static readonly UserId TestUserId = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        public ValueTask<User?> GetUserAsync(UserId id, CancellationToken cancellationToken = default) => ValueTask.FromResult<User?>(id == TestUserId ? new User(TestUserId, "test", "Test", UserStatus.Enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null);
        public ValueTask<User?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default) => ValueTask.FromResult<User?>(null);
        public ValueTask<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<User>>([]);
        public ValueTask<Session?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default) => ValueTask.FromResult<Session?>(token == "valid-session-token-for-test-user" ? new Session(SessionId.New(), TestUserId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null) : null);
        public ValueTask<User?> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default) => ValueTask.FromResult<User?>(null);
    }
}
