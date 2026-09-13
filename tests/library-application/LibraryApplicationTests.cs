using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.Media.Domain.Timelines;
using Xunit;

namespace Hispania.Library.Application.Tests;

public sealed class ProbedMediaMapperTests
{
    [Fact]
    public void IdentityFactoryCreatesDistinctVersion7DomainIds()
    {
        var identity = new ImportedPlayableMovieIdentityFactory().Create();
        var ids = new[]
        {
            identity.WorkId.Value, identity.MediaEntityId.Value, identity.TimelineId.Value, identity.MediaSourceId.Value,
            identity.MainVideo.AssetId.Value, identity.MainVideo.TechnicalRepresentationId.Value, identity.MainVideo.AssetCoverageId.Value,
            identity.MainAudio.AssetId.Value, identity.MainAudio.TechnicalRepresentationId.Value, identity.MainAudio.AssetCoverageId.Value
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(7, id.Version));
    }

    [Fact]
    public void MapsAValidH264AacMp4IntoOneCompleteMediaGraph()
    {
        var identity = Identity();
        var outcome = new ProbedMediaMapper().Map(Candidate("Films/feature.mp4"), Success(), identity);

        var entry = Assert.IsType<MediaMappingSucceeded>(outcome).Entry;
        var media = entry.Media;
        Assert.Equal("feature", entry.DisplayName);
        Assert.Equal("movie", media.Work.Category.Code);
        Assert.Equal("movie-realisation", media.MediaEntity.Category.Code);
        Assert.Equal(TimeSpan.FromSeconds(123), media.Timeline.Duration!.Value);
        Assert.Equal("library", media.Source.Locator.Scheme.Value);
        Assert.Equal("movies/Films/feature.mp4", media.Source.Locator.OpaqueValue);
        Assert.DoesNotContain(":\\", media.Source.Locator.OpaqueValue, StringComparison.Ordinal);

        var video = Assert.IsType<VideoTechnicalProfile>(media.VideoAsset.TechnicalRepresentations.Single().Profile);
        Assert.Equal(AssetMediaKind.Video, media.VideoAsset.MediaKind);
        Assert.Equal("main", media.VideoAsset.Roles.Single().Code);
        Assert.Equal(1920, video.Dimensions.Width);
        Assert.Equal(1080, video.Dimensions.Height);
        Assert.Equal(24000, video.FrameRate.Numerator);
        Assert.Equal(1001, video.FrameRate.Denominator);
        Assert.Equal("ffmpeg", video.Codec.Namespace);
        Assert.Equal("h264", video.Codec.Code);

        var audio = Assert.IsType<AudioTechnicalProfile>(media.AudioAsset.TechnicalRepresentations.Single().Profile);
        Assert.Equal(AssetMediaKind.Audio, media.AudioAsset.MediaKind);
        Assert.Equal("main", media.AudioAsset.Roles.Single().Code);
        Assert.Equal(48000, audio.SampleRateHertz);
        Assert.Equal(2, audio.ChannelCount);
        Assert.Equal("ffmpeg", audio.Codec.Namespace);
        Assert.Equal("aac", audio.Codec.Code);

        AssertCoverage(media.Timeline, media.VideoAsset.Id);
        AssertCoverage(media.Timeline, media.AudioAsset.Id);
        AssertBinding(media.VideoAsset, media.Source.Id, 0);
        AssertBinding(media.AudioAsset, media.Source.Id, 1);
        Assert.Equal(identity.WorkId, media.Work.Id);
        Assert.Equal(identity.MediaEntityId, media.MediaEntity.Id);
        Assert.Equal(identity.TimelineId, media.Timeline.Id);
        Assert.Equal(identity.MediaSourceId, media.Source.Id);
        Assert.Equal(identity.MainVideo.AssetId, media.VideoAsset.Id);
        Assert.Equal(identity.MainVideo.TechnicalRepresentationId, media.VideoAsset.TechnicalRepresentations.Single().Id);
        Assert.Equal(identity.MainVideo.AssetCoverageId, media.Timeline.Coverages.Single(coverage => coverage.AssetId == media.VideoAsset.Id).Id);
        Assert.Equal(identity.MainAudio.AssetId, media.AudioAsset.Id);
        Assert.Equal(identity.MainAudio.TechnicalRepresentationId, media.AudioAsset.TechnicalRepresentations.Single().Id);
        Assert.Equal(identity.MainAudio.AssetCoverageId, media.Timeline.Coverages.Single(coverage => coverage.AssetId == media.AudioAsset.Id).Id);
    }

    [Fact]
    public void MapsAbsentDurationAsUnknown()
    {
        var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(Candidate(), Success(durationPresent: false), Identity())).Entry;

        Assert.Null(entry.Media.Timeline.Duration);
    }

    [Fact]
    public void PreservesDiscoverySnapshotOutsideMediaDomain()
    {
        var observedAt = DateTimeOffset.UnixEpoch;
        var candidate = new DiscoveredMediaCandidate(new LibraryFileReference("movies", "feature.mp4"), 42, observedAt);

        var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(candidate, Success(), Identity())).Entry;

        Assert.Equal(42, entry.Snapshot.ByteLength);
        Assert.Equal(observedAt, entry.Snapshot.LastModifiedUtc);
    }

    [Theory]
    [InlineData(0, 1080, 8, 48000, 2)]
    [InlineData(1920, 1080, 0, 48000, 2)]
    [InlineData(1920, 1080, 8, 0, 2)]
    [InlineData(1920, 1080, 8, 48000, 0)]
    public void RejectsInvalidObservationsWithoutReturningPartialGraph(int width, int height, int bitDepth, int sampleRate, int channels)
    {
        var streams = new ProbeStreamObservation[]
        {
            new VideoStreamObservation(0, "h264", width, height, new ObservedRational(24000, 1001), bitDepth, null),
            new AudioStreamObservation(1, "aac", sampleRate, channels, null, null)
        };

        var outcome = new ProbedMediaMapper().Map(Candidate(), new ProbeSucceeded(new ProbeResult("mp4", null, streams)), Identity());

        Assert.Equal(MediaMappingFailureKind.InvalidObservation, Assert.IsType<MediaMappingRejected>(outcome).Kind);
    }

    [Fact]
    public void RejectsUnsupportedCodecs()
    {
        var outcome = new ProbedMediaMapper().Map(Candidate(), Success(videoCodec: "hevc"), Identity());

        Assert.Equal(MediaMappingFailureKind.UnsupportedMedia, Assert.IsType<MediaMappingRejected>(outcome).Kind);
    }

    [Fact]
    public void UsesTheFirstSupportedStreamByStreamIndex()
    {
        var streams = new ProbeStreamObservation[]
        {
            new VideoStreamObservation(4, "h264", 1280, 720, new ObservedRational(25, 1), 8, null),
            new AudioStreamObservation(5, "aac", 44100, 2, null, null),
            new VideoStreamObservation(0, "h264", 1920, 1080, new ObservedRational(24000, 1001), 8, null),
            new AudioStreamObservation(1, "aac", 48000, 2, null, null)
        };

        var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(Candidate(), new ProbeSucceeded(new ProbeResult("mp4", null, streams)), Identity())).Entry;

        AssertBinding(entry.Media.VideoAsset, entry.Media.Source.Id, 0);
        AssertBinding(entry.Media.AudioAsset, entry.Media.Source.Id, 1);
    }

    private static DiscoveredMediaCandidate Candidate(string relativePath = "feature.mp4") => new(new LibraryFileReference("movies", relativePath));

    private static ImportedPlayableMovieIdentity Identity() => new ImportedPlayableMovieIdentityFactory().Create();

    private static ProbeSucceeded Success(string videoCodec = "h264", bool durationPresent = true) => new(new ProbeResult("mov,mp4,m4a", durationPresent ? TimeSpan.FromSeconds(123) : null,
    [
        new VideoStreamObservation(0, videoCodec, 1920, 1080, new ObservedRational(24000, 1001), 8, 4_000_000),
        new AudioStreamObservation(1, "aac", 48000, 2, null, null)
    ]));

    private static void AssertCoverage(Hispania.Media.Domain.Timelines.Timeline timeline, Hispania.Media.Domain.Identity.AssetId assetId)
    {
        var coverage = Assert.Single(timeline.Coverages, item => item.AssetId == assetId);
        Assert.Equal(AssetCoverageState.Covered, coverage.State);
        Assert.Equal(CoverageKind.Full, coverage.CoverageKind);
        Assert.Same(TemporalAlignment.Identity, coverage.Alignment);
    }

    private static void AssertBinding(Asset asset, Hispania.Media.Domain.Identity.MediaSourceId sourceId, int streamIndex)
    {
        var binding = Assert.Single(asset.TechnicalRepresentations.Single().SourceBindings);
        Assert.Equal(sourceId, binding.MediaSourceId);
        Assert.Equal("ffmpeg", binding.ContainerExposure!.ContainerFormat.Namespace);
        Assert.Equal("mp4", binding.ContainerExposure.ContainerFormat.Code);
        Assert.Equal(streamIndex, binding.ContainerExposure.StreamLocator.StreamIndex);
    }
}

public sealed class LibraryScannerTests
{
    [Fact]
    public async Task FirstScanCreatesIdsAndAnUnchangedRescanReusesThem()
    {
        var catalog = new InMemoryMediaCatalog();
        var candidate = Candidate("feature.mp4", byteLength: 42, lastModifiedUtc: DateTimeOffset.UnixEpoch);

        await Scanner([candidate], [Success()], catalog).ScanAsync();
        var firstId = Assert.Single(catalog.GetAll()).Id;

        await Scanner([candidate], [Success()], catalog).ScanAsync();

        Assert.Equal(firstId, Assert.Single(catalog.GetAll()).Id);
    }

    [Fact]
    public async Task ChangedObservedSnapshotCreatesANewIdentityBundle()
    {
        var catalog = new InMemoryMediaCatalog();
        await Scanner([Candidate("feature.mp4", byteLength: 42, lastModifiedUtc: DateTimeOffset.UnixEpoch)], [Success()], catalog).ScanAsync();
        var firstId = Assert.Single(catalog.GetAll()).Id;

        await Scanner([Candidate("feature.mp4", byteLength: 43, lastModifiedUtc: DateTimeOffset.UnixEpoch)], [Success()], catalog).ScanAsync();

        Assert.NotEqual(firstId, Assert.Single(catalog.GetAll()).Id);
    }

    [Fact]
    public async Task ProbeFailureRemainsObservedAndPreservesTheLastValidCatalogEntry()
    {
        var catalog = new InMemoryMediaCatalog();
        var candidate = Candidate("feature.mp4", byteLength: 42, lastModifiedUtc: DateTimeOffset.UnixEpoch);
        await Scanner([candidate], [Success()], catalog).ScanAsync();
        var firstId = Assert.Single(catalog.GetAll()).Id;

        await Scanner([candidate], [new ProbeFailed(new ProbeFailure(ProbeFailureKind.CorruptMedia))], catalog).ScanAsync();

        Assert.Equal(firstId, Assert.Single(catalog.GetAll()).Id);
        var resolution = Assert.Single(await catalog.ResolveImportIdentitiesAsync(new LibraryScanScope(["movies"]), [new ImportIdentityRequest(new ImportKey("movies", "feature.mp4"), new LibrarySourceSnapshot(42, DateTimeOffset.UnixEpoch))]));
        Assert.Equal(LibraryImportStatus.ProbeFailed, resolution.LastKnownStatus);
    }

    [Fact]
    public async Task UnsupportedImportRemainsObservedInsteadOfBecomingDisappeared()
    {
        var catalog = new InMemoryMediaCatalog();
        var candidate = Candidate("feature.mp4", byteLength: 42, lastModifiedUtc: DateTimeOffset.UnixEpoch);

        await Scanner([candidate], [new ProbeFailed(new ProbeFailure(ProbeFailureKind.UnsupportedFormat))], catalog).ScanAsync();

        var resolution = Assert.Single(await catalog.ResolveImportIdentitiesAsync(new LibraryScanScope(["movies"]), [new ImportIdentityRequest(new ImportKey("movies", "feature.mp4"), new LibrarySourceSnapshot(42, DateTimeOffset.UnixEpoch))]));
        Assert.Equal(LibraryImportStatus.Unsupported, resolution.LastKnownStatus);
        Assert.Empty(catalog.GetAll());
    }

    [Fact]
    public async Task EmptyDiscoveryReplacesTheCatalogWithAnEmptySnapshot()
    {
        var catalog = new InMemoryMediaCatalog();
        var result = await Scanner([], [], catalog).ScanAsync();

        Assert.Equal(new LibraryScanResult(0, 0, 0, 0), result);
        Assert.Empty(catalog.GetAll());
    }

    [Fact]
    public void CatalogUpsertReplacesAnEntryWithTheSameMediaEntityId()
    {
        var catalog = new InMemoryMediaCatalog();
        var mapper = new ProbedMediaMapper();
        var entry = Assert.IsType<MediaMappingSucceeded>(mapper.Map(Candidate("first.mp4"), Success(), Identity())).Entry;
        var replacement = entry with { DisplayName = "updated" };

        catalog.Upsert(entry);
        catalog.Upsert(replacement);

        Assert.Equal("updated", Assert.Single(catalog.GetAll()).DisplayName);
    }

    [Fact]
    public async Task ImportsMultipleCandidatesAndExposesOnlyAFileNameDisplayLabel()
    {
        var candidates = new[] { Candidate("A/one.mp4"), Candidate("B/two.mp4") };
        var catalog = new InMemoryMediaCatalog();
        var result = await Scanner(candidates, candidates.Select(_ => Success()), catalog).ScanAsync();

        Assert.Equal(new LibraryScanResult(2, 2, 0, 0), result);
        Assert.Equal(["one", "two"], catalog.GetAll().Select(entry => entry.DisplayName).Order());
        Assert.All(catalog.GetAll(), entry => Assert.DoesNotContain("C:\\", entry.DisplayName, StringComparison.Ordinal));
        Assert.NotNull(catalog.GetById(catalog.GetAll().First().Id));
    }

    [Fact]
    public async Task ProbeAndMappingFailuresDoNotAbortTheScan()
    {
        var candidates = new[] { Candidate("good.mp4"), Candidate("unsupported.mp4"), Candidate("broken.mp4"), Candidate("invalid.mp4") };
        var outcomes = new ProbeOutcome[]
        {
            Success(),
            new ProbeFailed(new ProbeFailure(ProbeFailureKind.UnsupportedFormat)),
            new ProbeFailed(new ProbeFailure(ProbeFailureKind.CorruptMedia)),
            new ProbeSucceeded(new ProbeResult("mp4", null, [new VideoStreamObservation(0, "h264", 0, 1080, new ObservedRational(24, 1), 8, null), new AudioStreamObservation(1, "aac", 48000, 2, null, null)]))
        };
        var catalog = new InMemoryMediaCatalog();

        var result = await Scanner(candidates, outcomes, catalog).ScanAsync();

        Assert.Equal(new LibraryScanResult(4, 1, 1, 2), result);
        Assert.Single(catalog.GetAll());
    }

    [Fact]
    public async Task ACompletedRescanReplacesThePreviousSnapshotIncludingDeletedFiles()
    {
        var catalog = new InMemoryMediaCatalog();
        await Scanner([Candidate("old.mp4")], [Success()], catalog).ScanAsync();

        await Scanner([Candidate("new.mp4")], [Success()], catalog).ScanAsync();

        var entry = Assert.Single(catalog.GetAll());
        Assert.Equal("new", entry.DisplayName);
    }

    [Fact]
    public async Task CancellationDoesNotReplaceTheLastCompleteSnapshot()
    {
        var catalog = new InMemoryMediaCatalog();
        await Scanner([Candidate("existing.mp4")], [Success()], catalog).ScanAsync();
        using var cancellation = new CancellationTokenSource();
        var discovery = new CancellingDiscovery(Candidate("cancelled.mp4"), cancellation);
        var scanner = CreateScanner(discovery, new FakeProbe([Success()]), catalog);

        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(cancellation.Token));
        Assert.Equal("existing", Assert.Single(catalog.GetAll()).DisplayName);
    }

    [Fact]
    public async Task FailedScanDoesNotReplaceTheLastCompleteSnapshot()
    {
        var catalog = new InMemoryMediaCatalog();
        await Scanner([Candidate("existing.mp4")], [Success()], catalog).ScanAsync();

        var scanner = CreateScanner(new FakeDiscovery([Candidate("failed.mp4")]), new ThrowingProbe(), catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync());
        Assert.Equal("existing", Assert.Single(catalog.GetAll()).DisplayName);
    }

    [Fact]
    public async Task LocalFilesystemDiscoveryIntegratesWithTheMapperAndCatalogWithoutFfprobe()
    {
        using var directory = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(directory.Path, "feature.mp4"), "fixture");
        var discovery = new LocalFileSystemMediaDiscovery(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory.Path)]));
        var catalog = new InMemoryMediaCatalog();
        var scanner = CreateScanner(discovery, new FakeProbe([Success()]), catalog);

        var result = await scanner.ScanAsync();

        Assert.Equal(1, result.Imported);
        Assert.Equal("movies/feature.mp4", Assert.Single(catalog.GetAll()).Media.Source.Locator.OpaqueValue);
    }

    private static LibraryScanner Scanner(IEnumerable<DiscoveredMediaCandidate> candidates, IEnumerable<ProbeOutcome> outcomes, InMemoryMediaCatalog catalog) =>
        CreateScanner(new FakeDiscovery(candidates), new FakeProbe(outcomes), catalog);

    private static LibraryScanner CreateScanner(IMediaDiscovery discovery, IMediaProbe probe, InMemoryMediaCatalog catalog) =>
        new(discovery, probe, new ProbedMediaMapper(), catalog, new ImportedPlayableMovieIdentityFactory(), new LibraryScanScope(["movies"]));

    private static DiscoveredMediaCandidate Candidate(string relativePath, long? byteLength = null, DateTimeOffset? lastModifiedUtc = null) =>
        new(new LibraryFileReference("movies", relativePath), byteLength, lastModifiedUtc);

    private static ImportedPlayableMovieIdentity Identity() => new ImportedPlayableMovieIdentityFactory().Create();

    private static ProbeSucceeded Success() => new(new ProbeResult("mp4", TimeSpan.FromSeconds(1),
    [
        new VideoStreamObservation(0, "h264", 1920, 1080, new ObservedRational(24, 1), 8, null),
        new AudioStreamObservation(1, "aac", 48000, 2, null, null)
    ]));

    private sealed class FakeDiscovery(IEnumerable<DiscoveredMediaCandidate> candidates) : IMediaDiscovery
    {
        public async IAsyncEnumerable<DiscoveredMediaCandidate> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return candidate;
                await Task.Yield();
            }
        }
    }

    private sealed class CancellingDiscovery(DiscoveredMediaCandidate candidate, CancellationTokenSource cancellation) : IMediaDiscovery
    {
        public async IAsyncEnumerable<DiscoveredMediaCandidate> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return candidate;
            cancellation.Cancel();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeProbe(IEnumerable<ProbeOutcome> outcomes) : IMediaProbe
    {
        private readonly Queue<ProbeOutcome> _outcomes = new(outcomes);

        public ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_outcomes.Dequeue());
        }
    }

    private sealed class ThrowingProbe : IMediaProbe
    {
        public ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Probe failure.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-application-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
