using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
using Hispania.ExtensionDemo.Contracts;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Playback.Application;
using Hispania.Media.Domain.Identity;
using Hispania.ModuleSystem.Contracts;
using Hispania.Playback.Contracts;
using Hispania.Runtime;
using Hispania.Transcoding.FFmpeg;
using Hispania.Transcoding.Worker.Contracts;
using Hispania.Server;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Jobs.Application;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.WorkerAgentJobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class DistributedWorkerProcessIntegrationTests
{
    private const string NodeId = "phase11-worker-a";
    private const string CapabilityId = "hispania.extension-demo.greeting";
    private const string CapabilityVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task IndependentMtlsWorkerExecutesOopCapabilityAndRecoversRetrySafeJobWithFencing()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-distributed-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var transferRegistry = new DistributedMediaTransferRegistry(Path.Combine(root, "coordinator-transfers"));
        using var serverCertificate = CreateCertificate("CN=Hispania test coordinator", server: true);
        using var clientCertificate = CreateCertificate("CN=" + NodeId, server: false);
        using var publisherKey = RSA.Create(2048);
        var coordinator = new DistributedWorkCoordinator();
        var greetingCapability = new DistributedCapabilityV1(CapabilityId, CapabilityVersion);
        var ffmpegCapability = new DistributedCapabilityV1(DistributedFfmpegJobV1.ContractId, DistributedFfmpegJobV1.ContractVersion);
        var platformHashCapability = new DistributedCapabilityV1(DistributedPlatformJobExecutor.HashCapabilityId, DistributedPlatformJobExecutor.HashCapabilityVersion);
        var capabilities = new[] { greetingCapability, ffmpegCapability, platformHashCapability };
        var clientThumbprint = Convert.ToHexString(SHA256.HashData(clientCertificate.RawData));
        coordinator.AuthorizeWorker(NodeId, capabilities, clientThumbprint);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["DistributedWorkers:LeaseSeconds"] = "5";
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateMode = ClientCertificateMode.AllowCertificate,
            ClientCertificateValidation = static (_, _, _) => true
        })));
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton<IDistributedWorkCoordinatorV1>(coordinator);
        builder.Services.AddSingleton<IDistributedMediaTransferBrokerV1>(transferRegistry);
        builder.Services.AddHostedService(_ => new DistributedWorkLeaseMonitor(coordinator, TimeSpan.FromMilliseconds(200)));
        var blockNextResultUpload = 0;
        var resultUploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resultUploadCut = new CancellationTokenSource();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "PUT" && context.Request.Path.Value?.Contains("/media/result", StringComparison.Ordinal) == true &&
                Interlocked.Exchange(ref blockNextResultUpload, 0) == 1)
                context.Request.Body = new BlockingRequestBody(context.Request.Body, resultUploadStarted, resultUploadCut.Token);
            await next(context).ConfigureAwait(false);
        });
        app.MapDistributedWorkerEndpoints();
        await app.StartAsync();
        Process? worker = null;
        WebApplication? playbackApp = null;
        var workerOutput = new StringBuilder();
        try
        {
            var baseAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using (var probeHandler = new HttpClientHandler { ClientCertificateOptions = ClientCertificateOption.Manual, ServerCertificateCustomValidationCallback = static (_, _, _, _) => true })
            {
                probeHandler.ClientCertificates.Add(clientCertificate);
                using var probeClient = new HttpClient(probeHandler) { BaseAddress = new Uri(baseAddress + "/internal/worker/v1/") };
                using var probeResponse = await probeClient.PostAsJsonAsync(NodeId + "/connect",
                    new DistributedWorkerConnectRequestV1(1, 1, new DistributedWorkerInventoryV1("Windows", "x64", 1, 1024, 1, [], [greetingCapability])));
                Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);
            }
            var pfxPath = Path.Combine(root, "worker.pfx");
            await File.WriteAllBytesAsync(pfxPath, clientCertificate.Export(X509ContentType.Pfx, string.Empty));
            var extensionRoot = Path.Combine(root, "worker-extensions");
            await CreateOutOfProcessGreetingPackageAsync(extensionRoot, publisherKey);
            var agentDll = GetBuiltProjectDll("apps", "worker-agent", "Hispania.WorkerAgent.dll");
            var moduleHostDll = GetBuiltProjectDll("apps", "module-host", "Hispania.ModuleHost.dll");
            var processStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            processStart.ArgumentList.Add(agentDll);
            processStart.Environment["DistributedWorker__CoordinatorUri"] = baseAddress;
            processStart.Environment["DistributedWorker__NodeId"] = NodeId;
            processStart.Environment["DistributedWorker__ClientCertificatePath"] = pfxPath;
            processStart.Environment["DistributedWorker__CoordinatorCertificateSha256Pins__0"] = Convert.ToHexString(SHA256.HashData(serverCertificate.RawData));
            processStart.Environment["DistributedWorker__ContractAssemblies__0"] = "Hispania.ExtensionDemo.Contracts";
            processStart.Environment["DistributedWorker__LeaseSeconds"] = "5";
            processStart.Environment["DistributedWorker__HeartbeatSeconds"] = "1";
            processStart.Environment["DistributedWorker__PollMilliseconds"] = "50";
            processStart.Environment["DistributedWorker__CapabilityProviderAssemblies__0"] = typeof(PlatformJobsCapabilityProviderV1).Assembly.Location;
            processStart.Environment["DistributedWorker__MaximumConcurrentJobs"] = "1";
            processStart.Environment["Modules__WorkerExtensionIds__0"] = CapabilityId;
            processStart.Environment["Modules__ExtensionsPath"] = extensionRoot;
            processStart.Environment["Modules__HostPath"] = moduleHostDll;
            processStart.Environment["Modules__RequirePublisherSignature"] = "true";
            processStart.Environment["Modules__TrustedPublisherKeys__hispania.experimental.greetings"] = publisherKey.ExportSubjectPublicKeyInfoPem();
            var first = new DistributedJobRequestV1("greeting-1", CapabilityId, CapabilityVersion, "GetMessage", JsonSerializer.SerializeToElement(new { }), CorrelationId: "http-trace-1");
            coordinator.Queue(first, allowRemote: true);
            worker = StartWorker(processStart, workerOutput);
            var firstCompleted = await WaitForJobAsync(coordinator, first.JobId, job => job.State == DistributedJobStateV1.Completed, TimeSpan.FromSeconds(25), () => { lock (workerOutput) return workerOutput.ToString(); });
            Assert.Equal("Hello from a separately hosted greeting extension.", firstCompleted.Result?.GetString());
            Assert.Equal("http-trace-1", firstCompleted.Request.CorrelationId);
            var genericWorker = Assert.Single(coordinator.ListWorkers());
            Assert.DoesNotContain(ffmpegCapability, genericWorker.Inventory!.Capabilities);

            var jobsStore = new SqlitePlatformJobStore(Path.Combine(root, "jobs.sqlite"));
            var jobsRegistry = new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1());
            var jobsOptions = new PlatformJobOptionsV1(PollInterval: TimeSpan.FromMilliseconds(25), ExecutionLease: TimeSpan.FromSeconds(5));
            var remoteJobs = new DistributedPlatformJobExecutor(coordinator, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(250));
            using var jobDispatcher = new LocalPlatformJobDispatcher(jobsStore, jobsRegistry, jobsOptions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalPlatformJobDispatcher>.Instance, remote: remoteJobs);
            var jobService = new PlatformJobService(jobsStore, jobsRegistry, jobsOptions, execution: jobDispatcher, remote: remoteJobs);
            await jobDispatcher.StartAsync(CancellationToken.None);
            try
            {
                var remoteJob = await jobService.CreateAsync(new(PlatformTestHashHandlerV1.TypeId,
                    "{\"text\":\"remote-proof\",\"algorithm\":\"sha256\"}", CorrelationId: "platform-job-trace",
                    ExecutionPolicy: PlatformJobExecutionPolicyV1.RemoteOnly));
                var remoteJobDone = await WaitForPlatformJobAsync(jobService, remoteJob.Job.JobId, PlatformJobStatusV1.Succeeded, TimeSpan.FromSeconds(15));
                Assert.Equal("remote", remoteJobDone.Executor); Assert.Equal(NodeId, remoteJobDone.WorkerId);
                Assert.Equal(DistributedPlatformJobExecutor.HashCapabilityId + "@" + DistributedPlatformJobExecutor.HashCapabilityVersion, remoteJobDone.Capability);
                Assert.True(remoteJobDone.Generation > 0);
                var remoteAttempt = Assert.Single(await jobService.GetAttemptsAsync(remoteJob.Job.JobId));
                Assert.Equal("remote", remoteAttempt.ExecutorKind); Assert.Equal(NodeId, remoteAttempt.WorkerId); Assert.NotNull(remoteAttempt.LeaseId);

                var preferredRemote = await jobService.CreateAsync(new(PlatformTestHashHandlerV1.TypeId,
                    "{\"text\":\"preferred-remote\",\"algorithm\":\"sha256\"}", ExecutionPolicy: PlatformJobExecutionPolicyV1.RemotePreferred));
                var preferredRemoteDone = await WaitForPlatformJobAsync(jobService, preferredRemote.Job.JobId, PlatformJobStatusV1.Succeeded, TimeSpan.FromSeconds(10));
                Assert.Equal("remote", preferredRemoteDone.Executor); Assert.Equal(NodeId, preferredRemoteDone.WorkerId);

                worker!.Kill(entireProcessTree: true); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); worker.Dispose(); worker = null;
                Assert.True(coordinator.DisconnectWorker(NodeId, clientThumbprint));
                var waitingRemote = await jobService.CreateAsync(new(PlatformTestHashHandlerV1.TypeId, "{\"text\":\"wait\",\"algorithm\":\"sha256\"}", ExecutionPolicy: PlatformJobExecutionPolicyV1.RemoteOnly));
                await Task.Delay(300);
                Assert.Equal(PlatformJobStatusV1.Pending, (await jobService.GetAsync(waitingRemote.Job.JobId))!.Status);
                Assert.Empty(await jobService.GetAttemptsAsync(waitingRemote.Job.JobId));
                var fallback = await jobService.CreateAsync(new(PlatformTestHashHandlerV1.TypeId, "{\"text\":\"fallback\",\"algorithm\":\"sha256\"}", ExecutionPolicy: PlatformJobExecutionPolicyV1.RemotePreferred));
                var fallbackDone = await WaitForPlatformJobAsync(jobService, fallback.Job.JobId, PlatformJobStatusV1.Succeeded, TimeSpan.FromSeconds(5));
                Assert.Equal("local", fallbackDone.Executor);
                worker = StartWorker(processStart, workerOutput);
                await WaitForWorkerCapabilityAsync(coordinator, platformHashCapability, TimeSpan.FromSeconds(10));
                var resumed = await WaitForPlatformJobAsync(jobService, waitingRemote.Job.JobId, PlatformJobStatusV1.Succeeded, TimeSpan.FromSeconds(15));
                Assert.Equal("remote", resumed.Executor); Assert.Equal(1, resumed.AttemptCount);
            }
            finally { await jobDispatcher.StopAsync(CancellationToken.None); }
            worker!.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            worker.Dispose(); worker = null;
            Assert.True(coordinator.DisconnectWorker(NodeId, clientThumbprint));
            processStart.Environment["DistributedWorker__Ffmpeg__Enabled"] = "true";
            processStart.Environment["DistributedWorker__CapabilityProviderAssemblies__0"] = typeof(DistributedFfmpegCapabilityHandler).Assembly.Location;
            processStart.Environment["DistributedWorker__Ffmpeg__WorkerAssemblyPath"] = GetBuiltProjectDll("apps", "ffmpeg-worker", "Hispania.FFmpeg.Worker.dll");
            processStart.Environment["DistributedWorker__Ffmpeg__ExecutablePath"] = "ffmpeg";
            processStart.Environment["DistributedWorker__Ffmpeg__CachePath"] = Path.Combine(root, "worker-private-cache");
            worker = StartWorker(processStart, workerOutput);
            await WaitForWorkerCapabilityAsync(coordinator, ffmpegCapability, TimeSpan.FromSeconds(15));

            var sourcePath = Path.Combine(root, "coordinator-library", "sample-source.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await CreateFfmpegFixtureAsync(sourcePath);
            var sourceLength = new FileInfo(sourcePath).Length;
            var sourceInfo = new FileInfo(sourcePath);
            var mediaEntry = new ProbedMediaMapper().Map(new DiscoveredMediaCandidate(new LibraryFileReference("movies", "sample-source.mp4"), sourceLength, sourceInfo.LastWriteTimeUtc),
                new ProbeSucceeded(new ProbeResult("mov,mp4,m4a,3gp,3g2,mj2", TimeSpan.FromSeconds(1),
                [new VideoStreamObservation(0, "mpeg4", 160, 90, new ObservedRational(24, 1), 8, null), new AudioStreamObservation(1, "aac", 44100, 1, null, null)])),
                new ImportedPlayableMovieIdentityFactory().Create());
            var entry = Assert.IsType<MediaMappingSucceeded>(mediaEntry).Entry;
            var catalog = new InMemoryMediaCatalog(); catalog.Upsert(entry);
            var sourceAccessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", Path.GetDirectoryName(sourcePath)!)]));
            using var transcoder = new FfmpegCompatibleMediaTranscoder(catalog, sourceAccessor, "ffmpeg", Path.Combine(root, "playback-cache"),
                policy: new FfmpegWorkerPolicy(), distributedCoordinator: coordinator, mediaTransfers: transferRegistry,
                distributedMode: DistributedTranscodingMode.RemotePreferred);
            var playbackBuilder = WebApplication.CreateBuilder();
            playbackBuilder.WebHost.UseTestServer();
            playbackBuilder.Services.AddSingleton<IMediaCatalogReader>(catalog);
            playbackBuilder.Services.AddSingleton<ISourceContentAccessor>(sourceAccessor);
            playbackBuilder.Services.AddSingleton<IPlaybackPlanner>(new AlwaysTranscodePlanner(entry.Id));
            playbackBuilder.Services.AddSingleton<IDirectPlayExecutor>(new DirectPlayExecutor(catalog, sourceAccessor));
            playbackBuilder.Services.AddSingleton<ICompatibleMediaTranscoderV1>(transcoder);
            playbackBuilder.Services.AddSingleton<ITranscodedContentStore>(transcoder);
            DomainAuthorizationTestHost.AddAuthorizedUser(playbackBuilder.Services);
            playbackApp = playbackBuilder.Build();
            playbackApp.MapDirectPlayEndpoints();
            await playbackApp.StartAsync();
            using var playbackClient = playbackApp.GetTestClient();
            DomainAuthorizationTestHost.Authenticate(playbackClient);
            using var playbackResponse = await playbackClient.GetAsync($"/api/v1/media/{entry.Id.Value}/stream");
            Assert.Equal(HttpStatusCode.OK, playbackResponse.StatusCode);
            Assert.Equal("video/mp4", playbackResponse.Content.Headers.ContentType?.MediaType);
            var outputPath = Path.Combine(root, "validated-playback-output.mp4");
            await File.WriteAllBytesAsync(outputPath, await playbackResponse.Content.ReadAsByteArrayAsync());
            var remoteCompleted = coordinator.ListJobs().Single(job => job.Request.ContractId == ffmpegCapability.ContractId && job.State == DistributedJobStateV1.Completed);
            Assert.False(string.IsNullOrWhiteSpace(remoteCompleted.Request.CorrelationId));
            Assert.False(remoteCompleted.Request.Payload.TryGetProperty("accessToken", out _));
            await AssertValidMp4Async(outputPath);

            // Let a real FFmpeg job encode, then stop its result upload inside the server's
            // request stream. This synchronizes on the actual upload phase, not a timing delay.
            var uploadCancelCache = Path.Combine(root, "playback-upload-cancel-cache");
            using var uploadCancelTranscoder = new FfmpegCompatibleMediaTranscoder(catalog, sourceAccessor, "ffmpeg", uploadCancelCache,
                policy: new FfmpegWorkerPolicy(), distributedCoordinator: coordinator, mediaTransfers: transferRegistry,
                distributedMode: DistributedTranscodingMode.RemoteOnly);
            using var uploadCancellation = new CancellationTokenSource();
            var jobIdsBeforeUpload = coordinator.ListJobs().Select(job => job.Request.JobId).ToHashSet(StringComparer.Ordinal);
            Interlocked.Exchange(ref blockNextResultUpload, 1);
            var uploadJob = uploadCancelTranscoder.CreateCompatibleRepresentationAsync(entry.Id, uploadCancellation.Token).AsTask();
            await resultUploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            uploadCancellation.Cancel();
            // TestServer does not always propagate a client cancellation into a blocked request
            // body. Cancel the deterministic network-read gate as the equivalent socket cut.
            resultUploadCut.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploadJob);
            var uploadInterrupted = coordinator.ListJobs().Single(job => job.Request.ContractId == ffmpegCapability.ContractId && job.State == DistributedJobStateV1.Cancelled);
            Assert.DoesNotContain(uploadInterrupted.Request.JobId, jobIdsBeforeUpload);
            await WaitForNoFilesAsync(Path.Combine(root, "coordinator-transfers"), "*.partial", TimeSpan.FromSeconds(5));
            Assert.Empty(Directory.EnumerateFiles(uploadCancelCache, "*.mp4"));

            var interruptedSourcePath = Path.Combine(root, "coordinator-library", "sample-source-retry.mp4");
            File.Copy(sourcePath, interruptedSourcePath);
            var interruptedInfo = new FileInfo(interruptedSourcePath);
            var interruptedEntry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(
                new DiscoveredMediaCandidate(new LibraryFileReference("movies", "sample-source-retry.mp4"), interruptedInfo.Length, interruptedInfo.LastWriteTimeUtc),
                new ProbeSucceeded(new ProbeResult("mov,mp4,m4a,3gp,3g2,mj2", TimeSpan.FromSeconds(1),
                [new VideoStreamObservation(0, "mpeg4", 160, 90, new ObservedRational(24, 1), 8, null), new AudioStreamObservation(1, "aac", 44100, 1, null, null)])),
                new ImportedPlayableMovieIdentityFactory().Create())).Entry;
            catalog.Upsert(interruptedEntry);
            var blockingAccessor = new BlockingFirstSourceAccessor(sourceAccessor);
            using var recoveryTranscoder = new FfmpegCompatibleMediaTranscoder(catalog, blockingAccessor, "ffmpeg", Path.Combine(root, "playback-recovery-cache"),
                policy: new FfmpegWorkerPolicy(), distributedCoordinator: coordinator, mediaTransfers: transferRegistry,
                distributedMode: DistributedTranscodingMode.RemotePreferred);
            var interruptedTranscodeTask = recoveryTranscoder.CreateCompatibleRepresentationAsync(interruptedEntry.Id).AsTask();
            await blockingAccessor.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            worker!.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            worker.Dispose(); worker = null;
            blockingAccessor.ReleaseFirstRead.TrySetResult(true);
            var recoveredLocally = await interruptedTranscodeTask.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.IsType<CompatibleTranscodeReady>(recoveredLocally);
            Assert.Contains(coordinator.ListJobs(), job => job.Request.ContractId == ffmpegCapability.ContractId && job.State == DistributedJobStateV1.Cancelled);
            await AssertValidMp4Async(Path.Combine(root, "playback-recovery-cache", ((CompatibleTranscodeReady)recoveredLocally).Content.Value + ".mp4"));
            worker = StartWorker(processStart, workerOutput);

            var interrupted = new DistributedJobRequestV1("greeting-interrupted", CapabilityId, CapabilityVersion, "WaitForMessageAsync", JsonSerializer.SerializeToElement(5_000), RetrySafe: true, CorrelationId: "http-trace-2");
            coordinator.Queue(interrupted, allowRemote: true);
            var running = await WaitForJobAsync(coordinator, interrupted.JobId, job => job.State == DistributedJobStateV1.Running, TimeSpan.FromSeconds(10),
                () => { lock (workerOutput) return workerOutput.ToString() + "\n" + coordinator.ListWorkers().Single(); });
            var staleGeneration = running.Generation;
            var heartbeatBeforeKill = coordinator.ListWorkers().Single().LastHeartbeatUtc;
            await WaitForWorkerHeartbeatAsync(coordinator, heartbeatBeforeKill, TimeSpan.FromSeconds(5));
            worker!.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _ = await WaitForJobAsync(coordinator, interrupted.JobId,
                job => job.State == DistributedJobStateV1.Pending && coordinator.ListWorkers().Single().Connected == false,
                TimeSpan.FromSeconds(20), () => coordinator.ListWorkers().Single().ToString() + " @ " + DateTimeOffset.UtcNow);
            Assert.False(coordinator.Complete(NodeId, interrupted.JobId, staleGeneration, JsonSerializer.SerializeToElement("obsolete")));

            worker = StartWorker(processStart, workerOutput);
            var recovered = await WaitForJobAsync(coordinator, interrupted.JobId,
                job => job.State == DistributedJobStateV1.Completed && job.Generation > staleGeneration,
                TimeSpan.FromSeconds(12), () => { lock (workerOutput) return workerOutput.ToString(); });
            Assert.Equal("Hello from a separately hosted greeting extension.", recovered.Result?.GetString());
            Assert.False(coordinator.Complete(NodeId, interrupted.JobId, staleGeneration, JsonSerializer.SerializeToElement("stale-after-reconnect")));
        }
        finally
        {
            if (worker is { HasExited: false })
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            worker?.Dispose();
            if (playbackApp is not null) await playbackApp.StopAsync();
            await app.StopAsync();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    private static async Task WaitForNoFilesAsync(string directory, string pattern, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (Directory.EnumerateFiles(directory, pattern).Any())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                Assert.Empty(Directory.EnumerateFiles(directory, pattern));
            await Task.Delay(25);
        }
    }

    private static async Task<PlatformJobV1> WaitForPlatformJobAsync(PlatformJobService jobs, Guid id, PlatformJobStatusV1 status, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await jobs.GetAsync(id);
            if (current?.Status == status) return current;
            await Task.Delay(25);
        }
        var last = await jobs.GetAsync(id);
        throw new TimeoutException($"Platform Job did not reach {status}; current status is {last?.Status} ({last?.ErrorMetadata}).");
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(250); }
            catch (UnauthorizedAccessException) when (attempt < 20) { await Task.Delay(250); }
        }
    }

    private static Process StartWorker(ProcessStartInfo start, StringBuilder output)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("Worker agent process did not start.");
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (output) output.AppendLine(eventArgs.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task CreateFfmpegFixtureAsync(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=160x90:d=1", "-f", "lavfi", "-i", "sine=frequency=1000:duration=1", "-c:v", "mpeg4", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-movflags", "+faststart", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg to make the media fixture.");
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0 && File.Exists(path), $"FFmpeg fixture generation failed: {stderr}");
    }

    private static async Task AssertValidMp4Async(string path)
    {
        var start = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=format_name:stream=codec_name", "-of", "default=nw=1:nk=1", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0 && output.Contains("mp4", StringComparison.OrdinalIgnoreCase) && output.Contains("h264", StringComparison.OrdinalIgnoreCase), $"Remote output validation failed: {error}; streams: {output}");
    }

    private static async Task<DistributedJobSnapshotV1> WaitForJobAsync(DistributedWorkCoordinator coordinator, string jobId,
        Func<DistributedJobSnapshotV1, bool> predicate, TimeSpan timeout, Func<string>? diagnostics = null)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < timeout)
        {
            var job = coordinator.ListJobs().SingleOrDefault(item => item.Request.JobId == jobId);
            if (job is not null && predicate(job)) return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Distributed job '{jobId}' did not reach the expected state. Current state: {coordinator.ListJobs().SingleOrDefault(item => item.Request.JobId == jobId)?.State}. Worker output: {diagnostics?.Invoke()}");
    }

    private static async Task WaitForWorkerHeartbeatAsync(DistributedWorkCoordinator coordinator, DateTimeOffset? previousHeartbeat, TimeSpan timeout)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < timeout)
        {
            if (coordinator.ListWorkers().Single().LastHeartbeatUtc > previousHeartbeat) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Worker agent did not renew its heartbeat after starting a job.");
    }

    private static async Task WaitForWorkerCapabilityAsync(DistributedWorkCoordinator coordinator, DistributedCapabilityV1 capability, TimeSpan timeout)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < timeout)
        {
            if (coordinator.ListWorkers().Any(worker => worker.Connected && worker.Inventory?.Capabilities.Contains(capability) == true)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"No connected WorkerAgent advertised capability {capability} after provider loading.");
    }

    private static X509Certificate2 CreateCertificate(string subject, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var usages = new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task CreateOutOfProcessGreetingPackageAsync(string extensionsRoot, RSA publisherKey)
    {
        var repository = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var modulePath = Path.Combine(repository, "modules", "extension-demo", "bin", configuration, "net10.0", "Hispania.ExtensionDemo.dll");
        var contractPath = typeof(IExtensionDemoV1).Assembly.Location;
        var moduleBytes = await File.ReadAllBytesAsync(modulePath);
        var contractBytes = await File.ReadAllBytesAsync(contractPath);
        var moduleHash = Convert.ToHexString(SHA256.HashData(moduleBytes)).ToLowerInvariant();
        var contractHash = Convert.ToHexString(SHA256.HashData(contractBytes)).ToLowerInvariant();
        ModuleManifest manifest = new()
        {
            Id = CapabilityId, Name = "Independent greeting extension", Version = "3.0.0",
            Implements = [CapabilityId + "@" + CapabilityVersion], Provides = [CapabilityId + "@" + CapabilityVersion],
            Requires = [], Dependencies = [], OptionalDependencies = [], Permissions = [], MinimumHostVersion = "1.0.0",
            Runtime = "dotnet-outofprocess", PackageFormatVersion = 1, RestartRequiredForUpdate = false,
            EntryAssembly = "Hispania.ExtensionDemo.dll", EntryType = "Hispania.ExtensionDemo.OutOfProcessGreetingModule",
            SharedAssemblies = [], ContractTypes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CapabilityId + "@" + CapabilityVersion] = "Hispania.ExtensionDemo.Contracts.IExtensionDemoV1"
            },
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Hispania.ExtensionDemo.dll"] = moduleHash,
                ["Hispania.ExtensionDemo.Contracts.dll"] = contractHash
            }
        };
        manifest = ModulePackageSignature.Sign(manifest, "hispania.experimental.greetings", publisherKey.ExportPkcs8PrivateKeyPem());
        var package = Path.Combine(extensionsRoot, CapabilityId, "3.0.0");
        Directory.CreateDirectory(package);
        await File.WriteAllBytesAsync(Path.Combine(package, "Hispania.ExtensionDemo.dll"), moduleBytes);
        await File.WriteAllBytesAsync(Path.Combine(package, "Hispania.ExtensionDemo.Contracts.dll"), contractBytes);
        await File.WriteAllTextAsync(Path.Combine(package, "module.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static string GetBuiltProjectDll(params string[] pathParts)
    {
        var repository = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine([repository, .. pathParts[..^1], "bin", configuration, "net10.0", pathParts[^1]]);
        Assert.True(File.Exists(path), $"Expected built project output at {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Hispania.sln"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository root not found from test base directory.");
    }

    private sealed class AlwaysTranscodePlanner(MediaEntityId expected) : IPlaybackPlanner
    {
        public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId) => mediaEntityId == expected
            ? new DirectPlayUnavailable(mediaEntityId)
            : new PlaybackMediaNotFound(mediaEntityId);
    }

    private sealed class BlockingFirstSourceAccessor(ISourceContentAccessor inner) : ISourceContentAccessor
    {
        private int opens;
        public TaskCompletionSource<bool> FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SourceContentAccessResult> OpenReadAsync(Hispania.Media.Domain.Sources.MediaSource source, CancellationToken cancellationToken = default)
        {
            var opened = await inner.OpenReadAsync(source, cancellationToken).ConfigureAwait(false);
            if (opened is not SourceContentOpened result || Interlocked.Increment(ref opens) != 1) return opened;
            return new SourceContentOpened(new ReadableMediaContent(new BlockingReadStream(result.Content.Stream, FirstReadStarted, ReleaseFirstRead), result.Content.Facts));
        }
    }

    private sealed class BlockingReadStream(Stream inner, TaskCompletionSource<bool> started, TaskCompletionSource<bool> release) : Stream
    {
        private int firstRead = 1;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref firstRead, 0) == 1) { started.TrySetResult(true); await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingRequestBody(Stream inner, TaskCompletionSource reachedUpload, CancellationToken uploadCut) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reachedUpload.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, uploadCut);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
