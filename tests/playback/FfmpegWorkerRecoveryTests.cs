using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Media.Domain.Identity;
using Hispania.Playback.Contracts;
using Hispania.Transcoding.FFmpeg;
using Xunit;

namespace Hispania.Playback.Tests;

[CollectionDefinition("FFmpeg worker environment", DisableParallelization = true)]
public sealed class FfmpegWorkerEnvironment { }

[Collection("FFmpeg worker environment")]
public sealed class FfmpegWorkerRecoveryTests : IDisposable
{
    private const string ModeVariable = "HISPANIA_FAKE_WORKER_MODE";
    private readonly string? previousMode = Environment.GetEnvironmentVariable(ModeVariable);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hispania-worker-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string cache;
    private readonly MediaEntityId mediaId;
    private readonly InMemoryMediaCatalog catalog = new();
    private readonly LocalFileSystemSourceContentAccessor accessor;
    private readonly string fakeWorker = Path.Combine(AppContext.BaseDirectory, "Hispania.FFmpeg.Worker.Fake.dll");

    [Fact]
    public void WorkerPolicyRejectsUnboundedOrContradictoryLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FfmpegWorkerPolicy { MaximumConcurrentJobs = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new FfmpegWorkerPolicy { MaximumCpuRatePercent = 101 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new FfmpegWorkerPolicy { MaximumTemporaryStorageBytes = 100, MaximumConcurrentJobs = 2, MaximumCacheBytes = 20 }.Validate());
    }

    public FfmpegWorkerRecoveryTests()
    {
        cache = Path.Combine(directory, "cache");
        var root = Path.Combine(directory, "movies");
        Directory.CreateDirectory(Path.Combine(root, "Film"));
        var path = Path.Combine(root, "Film", "Film.mkv");
        File.WriteAllBytes(path, Enumerable.Range(0, 512).Select(value => (byte)value).ToArray());
        var info = new FileInfo(path);
        var identity = new ImportedPlayableMovieIdentityFactory().Create();
        var candidate = new DiscoveredMediaCandidate(new LibraryFileReference("movies", "Film/Film.mkv"), info.Length, info.LastWriteTimeUtc);
        var probe = new ProbeSucceeded(new ProbeResult("matroska", TimeSpan.FromSeconds(1),
        [
            new VideoStreamObservation(0, "h264", 64, 64, new ObservedRational(24, 1), 8, null),
            new AudioStreamObservation(1, "aac", 48000, 2, null, null)
        ]));
        var mapped = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(candidate, probe, identity));
        catalog.Upsert(mapped.Entry);
        mediaId = mapped.Entry.Id;
        accessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", root)]));
    }

    [Fact]
    public async Task CrashedWorkerCleansPartialsAndSameRequestCanRecoverIdempotently()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "crash");
        using var transcoder = CreateTranscoder();
        Assert.IsType<CompatibleTranscodeUnavailable>(await transcoder.CreateCompatibleRepresentationAsync(mediaId));
        Assert.Empty(Directory.EnumerateFiles(cache, "*.partial.mp4"));

        Environment.SetEnvironmentVariable(ModeVariable, "success");
        var ready = Assert.IsType<CompatibleTranscodeReady>(await transcoder.CreateCompatibleRepresentationAsync(mediaId));
        var repeated = Assert.IsType<CompatibleTranscodeReady>(await transcoder.CreateCompatibleRepresentationAsync(mediaId));
        Assert.Equal(ready.Content, repeated.Content);
        Assert.Single(Directory.EnumerateFiles(cache, "*.mp4"));
        Assert.Equal("available", transcoder.GetSnapshot().Status);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationTerminateWorkerAndRemovePartials()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "wait");
        using var transcoder = CreateTranscoder(new FfmpegWorkerPolicy { MaximumJobDuration = TimeSpan.FromMilliseconds(250) });
        Assert.IsType<CompatibleTranscodeUnavailable>(await transcoder.CreateCompatibleRepresentationAsync(mediaId));
        Assert.Empty(Directory.EnumerateFiles(cache, "*.partial.mp4"));

        using var cancellationTranscoder = CreateTranscoder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationTranscoder.CreateCompatibleRepresentationAsync(mediaId, cancellation.Token).AsTask());
        Assert.Empty(Directory.EnumerateFiles(cache, "*.partial.mp4"));
    }

    [Fact]
    public async Task FailedPublicationLeavesNoPartialAndCanRecover()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "publication-failure");
        using var transcoder = CreateTranscoder();
        Assert.IsType<CompatibleTranscodeUnavailable>(await transcoder.CreateCompatibleRepresentationAsync(mediaId));
        Assert.Empty(Directory.EnumerateFiles(cache, "*.partial.mp4"));

        foreach (var path in Directory.EnumerateDirectories(cache)) Directory.Delete(path, recursive: true);
        Environment.SetEnvironmentVariable(ModeVariable, "success");
        Assert.IsType<CompatibleTranscodeReady>(await transcoder.CreateCompatibleRepresentationAsync(mediaId));
    }

    private FfmpegCompatibleMediaTranscoder CreateTranscoder(FfmpegWorkerPolicy? policy = null) =>
        new(catalog, accessor, "unused-under-fake-worker", cache, fakeWorker, policy);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ModeVariable, previousMode);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
