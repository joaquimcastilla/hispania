using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.Media.Domain.Timelines;
using Hispania.Media.Domain.Works;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class DirectPlayPlannerTests
{
    [Fact]
    public void CompatibleMediaProducesPlanWithOnlySelectedIds()
    {
        var entry = Entry();
        var result = new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id);

        var plan = Assert.IsType<DirectPlayPlanned>(result).Plan;
        Assert.Equal(entry.Id, plan.MediaEntityId);
        Assert.Equal(entry.Media.Source.Id, plan.MediaSourceId);
        Assert.Equal(entry.Media.VideoAsset.TechnicalRepresentations.Single().Id, plan.VideoTechnicalRepresentationId);
        Assert.Equal(entry.Media.AudioAsset.TechnicalRepresentations.Single().Id, plan.AudioTechnicalRepresentationId);
        Assert.DoesNotContain(typeof(DirectPlayPlan).GetProperties(), property => property.PropertyType == typeof(SourceLocator) || property.PropertyType == typeof(LibraryFileReference));
    }

    [Fact]
    public void MissingMediaIsReported() => Assert.IsType<PlaybackMediaNotFound>(new DirectPlayPlanner(Catalog()).Plan(MediaEntityId.New()));

    [Theory]
    [InlineData("hevc", "aac", "mp4")]
    [InlineData("h264", "opus", "mp4")]
    [InlineData("h264", "aac", "mkv")]
    [InlineData("mpeg4", "mp3", "avi")]
    public void UnsupportedCombinationIsNotPlanned(string videoCodec, string audioCodec, string container)
    {
        var entry = Entry(videoCodec, audioCodec, container);
        Assert.IsType<DirectPlayUnavailable>(new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id));
    }

    [Fact]
    public void PlannerDoesNotOpenSources()
    {
        var entry = Entry();
        var planner = new DirectPlayPlanner(Catalog(entry));
        _ = planner.Plan(entry.Id);
    }

    private static InMemoryMediaCatalog Catalog(params MediaCatalogEntry[] entries)
    {
        var catalog = new InMemoryMediaCatalog();
        foreach (var entry in entries) catalog.Upsert(entry);
        return catalog;
    }

    public static MediaCatalogEntry Entry(string videoCodec = "h264", string audioCodec = "aac", string container = "mp4", byte[]? bytes = null, LibrarySourceSnapshot? snapshot = null)
    {
        var file = new LibraryFileReference("movies", "film.mp4");
        var work = new Work(WorkId.New(), WorkCategory.Movie);
        var entity = work.AddMediaEntity(MediaEntityId.New(), MediaEntityCategory.MovieRealisation);
        var timeline = entity.AddTimeline(TimelineId.New(), new TimelineDuration(TimeSpan.FromSeconds(2)));
        var source = new MediaSource(MediaSourceId.New(), LibrarySourceLocator.From(file));
        var videoAsset = new Asset(AssetId.New(), AssetMediaKind.Video, [AssetRole.Main]);
        var video = videoAsset.AddTechnicalRepresentation(TechnicalRepresentationId.New(), new VideoTechnicalProfile(new PixelDimensions(1920, 1080), new CodecIdentifier("ffmpeg", videoCodec), new FrameRate(24, 1), 8));
        var audioAsset = new Asset(AssetId.New(), AssetMediaKind.Audio, [AssetRole.Main]);
        var audio = audioAsset.AddTechnicalRepresentation(TechnicalRepresentationId.New(), new AudioTechnicalProfile(new CodecIdentifier("ffmpeg", audioCodec), 48000, 2));
        var exposure = new ContainerExposure(new ContainerFormat("ffmpeg", container), new StreamLocator(0));
        video.AddSourceBinding(source, exposure);
        audio.AddSourceBinding(source, exposure);
        var observed = bytes ?? [1, 2, 3, 4, 5];
        var modified = DateTimeOffset.UnixEpoch;
        return new MediaCatalogEntry(entity.Id, "film", file, snapshot ?? new LibrarySourceSnapshot(observed.Length, modified), new ImportedMedia(work, entity, timeline, videoAsset, audioAsset, source));
    }
}

public sealed class DirectPlayExecutorTests
{
    [Fact]
    public async Task FullContentAndPartialRangesAreResolvedWithoutOverread()
    {
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        var entry = DirectPlayPlannerTests.Entry(bytes: bytes);
        var executor = Executor(entry, bytes);
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id)).Plan;

        await using var full = await executor.ExecuteAsync(plan, new ByteRangeRequest.Full());
        Assert.Equal(10, Assert.IsType<FullContent>(full).TotalLength);
        await using var partial = await executor.ExecuteAsync(plan, new ByteRangeRequest.Closed(2, 99));
        var content = Assert.IsType<PartialContent>(partial);
        Assert.Equal((2L, 9L, 8L), (content.Start, content.End, content.SelectedLength));
        using var output = new MemoryStream();
        await content.Content.Stream.CopyToAsync(output);
        Assert.Equal(bytes[2..], output.ToArray());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task FromAtOrPastEndIsNotSatisfiable(long start)
    {
        var entry = DirectPlayPlannerTests.Entry();
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id)).Plan;
        await using var result = await Executor(entry, [1, 2, 3, 4, 5]).ExecuteAsync(plan, new ByteRangeRequest.From(start));
        Assert.IsType<RangeNotSatisfiable>(result);
    }

    [Fact]
    public async Task ChangedOrIncompleteSnapshotsAreRejected()
    {
        var changed = DirectPlayPlannerTests.Entry(snapshot: new LibrarySourceSnapshot(4, DateTimeOffset.UnixEpoch));
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(changed)).Plan(changed.Id)).Plan;
        await using var changedResult = await Executor(changed, [1, 2, 3, 4, 5]).ExecuteAsync(plan, new ByteRangeRequest.Full());
        Assert.IsType<SourceChanged>(changedResult);

        var incomplete = DirectPlayPlannerTests.Entry(snapshot: new LibrarySourceSnapshot(null, DateTimeOffset.UnixEpoch));
        var incompletePlan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(incomplete)).Plan(incomplete.Id)).Plan;
        await using var incompleteResult = await Executor(incomplete, [1, 2, 3, 4, 5]).ExecuteAsync(incompletePlan, new ByteRangeRequest.Full());
        Assert.IsType<SourceChanged>(incompleteResult);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(5, 5)]
    public async Task InvalidClosedAndEmptyRangesAreNotSatisfiable(long start, long end)
    {
        var entry = DirectPlayPlannerTests.Entry();
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id)).Plan;
        await using var result = await Executor(entry, [1, 2, 3, 4, 5]).ExecuteAsync(plan, new ByteRangeRequest.Closed(start, end));
        Assert.IsType<RangeNotSatisfiable>(result);
    }

    [Fact]
    public async Task SuffixAndFullEmptyContentHaveTheSpecifiedSemantics()
    {
        var entry = DirectPlayPlannerTests.Entry(bytes: [1, 2, 3, 4, 5]);
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id)).Plan;
        await using var suffix = await Executor(entry, [1, 2, 3, 4, 5]).ExecuteAsync(plan, new ByteRangeRequest.Suffix(10));
        var partial = Assert.IsType<PartialContent>(suffix);
        Assert.Equal((0L, 4L), (partial.Start, partial.End));

        var empty = DirectPlayPlannerTests.Entry(bytes: []);
        var emptyPlan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(empty)).Plan(empty.Id)).Plan;
        await using var full = await Executor(empty, []).ExecuteAsync(emptyPlan, new ByteRangeRequest.Full());
        Assert.Equal(0, Assert.IsType<FullContent>(full).TotalLength);
    }

    [Fact]
    public async Task MissingCatalogEntryMakesPlanInvalid()
    {
        var entry = DirectPlayPlannerTests.Entry();
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(Catalog(entry)).Plan(entry.Id)).Plan;
        await using var result = await Executor(null, [1]).ExecuteAsync(plan, new ByteRangeRequest.Full());
        Assert.IsType<PlanNoLongerValid>(result);
    }

    private static DirectPlayExecutor Executor(MediaCatalogEntry? entry, byte[] bytes) => new(Catalog(entry), new FakeAccessor(bytes, DateTimeOffset.UnixEpoch));
    private static InMemoryMediaCatalog Catalog(MediaCatalogEntry? entry = null) { var catalog = new InMemoryMediaCatalog(); if (entry is not null) catalog.Upsert(entry); return catalog; }
    private sealed class FakeAccessor(byte[] bytes, DateTimeOffset lastModified) : ISourceContentAccessor
    {
        public ValueTask<SourceContentAccessResult> OpenReadAsync(MediaSource source, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SourceContentAccessResult>(new SourceContentOpened(new ReadableMediaContent(new MemoryStream(bytes, writable: false), new SourceContentFacts(bytes.Length, lastModified))));
    }
}

public sealed class LocalSourceContentAccessorTests
{
    [Fact]
    public async Task OpensAValidLibrarySourceWithoutExposingPath()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "film.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var source = new MediaSource(MediaSourceId.New(), LibrarySourceLocator.From(new LibraryFileReference("movies", "film.mp4")));
        var accessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory.Path)]));

        var opened = Assert.IsType<SourceContentOpened>(await accessor.OpenReadAsync(source));
        await using var content = opened.Content;
        Assert.True(content.CanSeek);
        Assert.Equal(3, content.Length);
        Assert.DoesNotContain(typeof(ReadableMediaContent).GetProperties(), property => property.Name.Contains("Path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownAndMissingSourcesAreTypedFailures()
    {
        using var directory = TemporaryDirectory.Create();
        var accessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory.Path)]));
        var unknown = new MediaSource(MediaSourceId.New(), new SourceLocator(new SourceScheme("unknown"), "x"));
        var missing = new MediaSource(MediaSourceId.New(), LibrarySourceLocator.From(new LibraryFileReference("movies", "missing.mp4")));

        Assert.IsType<SourceContentUnsupported>(await accessor.OpenReadAsync(unknown));
        Assert.IsType<SourceContentUnavailable>(await accessor.OpenReadAsync(missing));
    }

    [Fact]
    public async Task TraversalMissingRootAndCancellationAreNotOpened()
    {
        using var directory = TemporaryDirectory.Create();
        var accessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory.Path)]));
        var traversal = new MediaSource(MediaSourceId.New(), new SourceLocator(new SourceScheme("library"), "movies/../outside.mp4"));
        Assert.IsType<SourceContentUnsupported>(await accessor.OpenReadAsync(traversal));

        var missingRootAccessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", Path.Combine(directory.Path, "missing"))]));
        var source = new MediaSource(MediaSourceId.New(), LibrarySourceLocator.From(new LibraryFileReference("movies", "film.mp4")));
        Assert.IsType<SourceContentUnavailable>(await missingRootAccessor.OpenReadAsync(source));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await accessor.OpenReadAsync(source, cancellation.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create() { var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-playback-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new TemporaryDirectory(path); }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}

public sealed class PlaybackArchitectureTests
{
    [Fact]
    public void PlaybackApplicationDoesNotReferenceLibraryApplicationOrLocalFilesystem()
    {
        var references = typeof(DirectPlayPlanner).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);

        Assert.DoesNotContain("Hispania.Library.Application", references);
        Assert.DoesNotContain("Hispania.Library.LocalFileSystem", references);
    }

    [Fact]
    public void PlaybackContractsDoNotReferenceAspNetAndMediaDomainHasNoFilesystemOrHttpReferences()
    {
        var playbackReferences = typeof(IPlaybackPlanner).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);
        var domainReferences = typeof(MediaSource).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);

        Assert.DoesNotContain(playbackReferences, name => name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(domainReferences, name => name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true || name == "System.IO.FileSystem");
    }
}
