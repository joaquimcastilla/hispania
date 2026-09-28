using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics.Metrics;
using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.TechnicalRepresentations;
using Hispania.ModuleSystem.Contracts;
using Hispania.Playback.Contracts;
using Hispania.Transcoding.Worker.Contracts;

namespace Hispania.Transcoding.FFmpeg;

/// <summary>Host-side capability client. Encoding executes in a separately supervised worker process.</summary>
public sealed class FfmpegCompatibleMediaTranscoder : ICompatibleMediaTranscoderV1, IModule, ITranscodedContentStore, ITranscodingCapabilityHealthV1, IDisposable
{
    private static readonly Meter WorkerMeter = new("Hispania.Transcoding.FFmpeg", "1.0");
    private static readonly ActivitySource WorkerActivities = new("Hispania.Transcoding.FFmpeg", "1.0");
    private static readonly Counter<long> WorkerAttempts = WorkerMeter.CreateCounter<long>("hispania.transcoding.worker.attempts");
    private static readonly Counter<long> WorkerFailures = WorkerMeter.CreateCounter<long>("hispania.transcoding.worker.failures");
    private static readonly Counter<long> WorkerRetries = WorkerMeter.CreateCounter<long>("hispania.transcoding.worker.retries");
    private static readonly Counter<long> WorkerCancellations = WorkerMeter.CreateCounter<long>("hispania.transcoding.worker.cancellations");
    private static readonly UpDownCounter<long> ActiveJobs = WorkerMeter.CreateUpDownCounter<long>("hispania.transcoding.worker.active_jobs");
    private static readonly Histogram<double> JobDuration = WorkerMeter.CreateHistogram<double>("hispania.transcoding.worker.duration", "ms");
    private static readonly Histogram<long> PeakWorkingSet = WorkerMeter.CreateHistogram<long>("hispania.transcoding.worker.peak_working_set", "By");
    private static readonly Histogram<double> CpuTime = WorkerMeter.CreateHistogram<double>("hispania.transcoding.worker.cpu_time", "ms");
    private static readonly Histogram<long> OutputSize = WorkerMeter.CreateHistogram<long>("hispania.transcoding.worker.output_size", "By");
    private readonly IMediaCatalogReader catalog;
    private readonly ISourceContentAccessor sourceAccessor;
    private readonly string ffmpegPath;
    private readonly string cacheDirectory;
    private readonly string workerAssemblyPath;
    private readonly FfmpegWorkerPolicy policy;
    private readonly SemaphoreSlim workerSlots;
    private readonly FfmpegTranscodingCapabilityHealth health;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly IDistributedWorkCoordinatorV1? distributedCoordinator;
    private readonly IDistributedMediaTransferBrokerV1? mediaTransfers;
    private readonly DistributedTranscodingMode distributedMode;

    public FfmpegCompatibleMediaTranscoder(IMediaCatalogReader catalog, ISourceContentAccessor sourceAccessor, string ffmpegPath, string cacheDirectory, string? workerAssemblyPath = null, FfmpegWorkerPolicy? policy = null, FfmpegTranscodingCapabilityHealth? health = null, IDistributedWorkCoordinatorV1? distributedCoordinator = null, IDistributedMediaTransferBrokerV1? mediaTransfers = null, DistributedTranscodingMode distributedMode = DistributedTranscodingMode.Local)
    {
        this.catalog = catalog;
        this.sourceAccessor = sourceAccessor;
        this.ffmpegPath = ffmpegPath;
        this.cacheDirectory = cacheDirectory;
        this.workerAssemblyPath = string.IsNullOrWhiteSpace(workerAssemblyPath) ? Path.Combine(AppContext.BaseDirectory, "Hispania.FFmpeg.Worker.dll") : Path.GetFullPath(workerAssemblyPath);
        this.policy = policy ?? new FfmpegWorkerPolicy();
        this.policy.Validate();
        workerSlots = new SemaphoreSlim(this.policy.MaximumConcurrentJobs, this.policy.MaximumConcurrentJobs);
        this.health = health ?? new FfmpegTranscodingCapabilityHealth();
        this.distributedCoordinator = distributedCoordinator;
        this.mediaTransfers = mediaTransfers;
        this.distributedMode = distributedMode;
    }

    public ModuleDescriptor Descriptor { get; } = new("hispania.transcoding.ffmpeg", "FFmpeg compatible media transcoder", new Version(1, 0, 0), [new ModuleCapability("hispania.playback.compatible-transcoder", new Version(1, 0))]);
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public void Dispose() => workerSlots.Dispose();
    public TranscodingCapabilityHealthSnapshot GetSnapshot() => health.GetSnapshot();

    public async ValueTask<CompatibleTranscodeResult> CreateCompatibleRepresentationAsync(MediaEntityId mediaEntityId, CancellationToken cancellationToken = default)
    {
        using var activity = WorkerActivities.StartActivity("transcoding.worker.job");
        var correlationId = activity?.TraceId.ToString() ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        activity?.SetTag("hispania.module.id", Descriptor.Id);
        activity?.SetTag("hispania.worker.protocol", FfmpegWorkerRequestV1.ContractId);
        var stopwatch = Stopwatch.StartNew();
        health.JobStarted();
        ActiveJobs.Add(1);
        try
        {
            var result = await CreateCoreAsync(mediaEntityId, correlationId, cancellationToken).ConfigureAwait(false);
            if (result is CompatibleTranscodeReady ready)
            {
                health.JobSucceeded();
                OutputSize.Record(ready.Length);
            }
            else
            {
                health.JobFailed("unavailable");
                WorkerFailures.Add(1, new KeyValuePair<string, object?>("failure_code", "unavailable"));
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            health.JobCancelled();
            WorkerCancellations.Add(1);
            throw;
        }
        catch
        {
            health.JobFailed("host_error");
            WorkerFailures.Add(1, new KeyValuePair<string, object?>("failure_code", "host_error"));
            throw;
        }
        finally
        {
            stopwatch.Stop();
            JobDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            ActiveJobs.Add(-1);
        }
    }

    private async ValueTask<CompatibleTranscodeResult> CreateCoreAsync(MediaEntityId mediaEntityId, string correlationId, CancellationToken cancellationToken)
    {
        var entry = catalog.GetById(mediaEntityId);
        if (entry is null || !IsSupportedSourceExtension(Path.GetExtension(entry.File.RelativePath)) || entry.Snapshot.ByteLength is not > 0 || entry.Snapshot.LastModifiedUtc is null)
            return new CompatibleTranscodeUnavailable();

        var keyText = $"{entry.File.RootKey}\n{entry.File.RelativePath}\n{entry.Snapshot.ByteLength}\n{entry.Snapshot.LastModifiedUtc.Value.UtcTicks}";
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText))).ToLowerInvariant();
        Directory.CreateDirectory(cacheDirectory);
        RemoveStalePartialFiles();
        var outputPath = GetCachePath(cacheKey);
        if (TryReady(cacheKey, outputPath) is { } existing) return existing;

        var gate = gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await workerSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            if (TryReady(cacheKey, outputPath) is { } cached) return cached;
            var remoteReady = await TryRemoteTranscodeAsync(entry, cacheKey, correlationId, outputPath, cancellationToken).ConfigureAwait(false);
            if (remoteReady is not null) return remoteReady;
            if (distributedMode == DistributedTranscodingMode.RemoteOnly) return new CompatibleTranscodeUnavailable();
            if (File.Exists(outputPath))
            {
                try { if (new FileInfo(outputPath).Length == 0) File.Delete(outputPath); }
                catch (IOException) { return new CompatibleTranscodeUnavailable(); }
                catch (UnauthorizedAccessException) { return new CompatibleTranscodeUnavailable(); }
            }
            var codec = entry.Media.VideoAsset.TechnicalRepresentations.FirstOrDefault()?.Profile is VideoTechnicalProfile video && video.Codec.Code == "h264" ? "copy" : "libx264";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                WorkerAttempts.Add(1);
                if (attempt > 0)
                {
                    health.Retried();
                    WorkerRetries.Add(1);
                }
                var opened = await sourceAccessor.OpenReadAsync(entry.Media.Source, cancellationToken).ConfigureAwait(false);
                if (opened is not SourceContentOpened source) return new CompatibleTranscodeUnavailable();
                await using var content = source.Content;
                if (content.Length != entry.Snapshot.ByteLength ||
                    (content.Facts.LastModifiedUtc is { } modified && modified.ToUniversalTime() != entry.Snapshot.LastModifiedUtc.Value.ToUniversalTime()))
                    return new CompatibleTranscodeUnavailable();

                try
                {
                    if (await RunWorkerAsync(cacheKey, codec, content.Stream, content.Length, correlationId, cancellationToken).ConfigureAwait(false))
                    {
                        PruneCache(outputPath);
                        if (TryReady(cacheKey, outputPath) is { } ready) return ready;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or TimeoutException)
                {
                    if (attempt == 1) return new CompatibleTranscodeUnavailable();
                }
            }
            return new CompatibleTranscodeUnavailable();
            }
            finally { workerSlots.Release(); }
        }
        finally { gate.Release(); }
    }

    private async ValueTask<CompatibleTranscodeReady?> TryRemoteTranscodeAsync(MediaCatalogEntry entry, string cacheKey, string correlationId, string outputPath, CancellationToken cancellationToken)
    {
        if (distributedMode == DistributedTranscodingMode.Local || distributedCoordinator is null || mediaTransfers is null) return null;
        var codec = entry.Media.VideoAsset.TechnicalRepresentations.FirstOrDefault()?.Profile is VideoTechnicalProfile video && video.Codec.Code == "h264" ? "copy" : "libx264";
        var compatible = distributedCoordinator.ListWorkers().Any(worker => worker.Authorized && worker.Enabled && worker.Connected &&
            worker.Inventory?.Capabilities.Contains(new DistributedCapabilityV1(DistributedFfmpegJobV1.ContractId, DistributedFfmpegJobV1.ContractVersion)) == true);
        if (!compatible) return null;
        var perJobTemporaryBytes = policy.MaximumTemporaryStorageBytes / policy.MaximumConcurrentJobs;
        if (entry.Snapshot.ByteLength!.Value > perJobTemporaryBytes / 2) return null;
        var grantLifetime = policy.MaximumJobDuration >= TimeSpan.FromHours(5) ? TimeSpan.FromHours(6) : policy.MaximumJobDuration + TimeSpan.FromMinutes(5);
        var jobId = "ffmpeg-" + Guid.NewGuid().ToString("N");
        var grant = mediaTransfers.RegisterSource(jobId, entry.Snapshot.ByteLength.Value, perJobTemporaryBytes / 2,
            async token =>
            {
                var opened = await sourceAccessor.OpenReadAsync(entry.Media.Source, token).ConfigureAwait(false);
                if (opened is not SourceContentOpened source || source.Content.Length != entry.Snapshot.ByteLength ||
                    (source.Content.Facts.LastModifiedUtc is { } modified && modified.ToUniversalTime() != entry.Snapshot.LastModifiedUtc!.Value.ToUniversalTime()))
                    throw new IOException("The source media is no longer available with the expected snapshot.");
                return source.Content.Stream;
            }, grantLifetime);
        var payload = new DistributedFfmpegJobV1(cacheKey, codec, grant.SourceLength, grant.TransferId,
            grant.MaximumResultBytes, correlationId);
        var request = new DistributedJobRequestV1(jobId, DistributedFfmpegJobV1.ContractId, DistributedFfmpegJobV1.ContractVersion,
            "Transcode", JsonSerializer.SerializeToElement(payload), RetrySafe: true, CorrelationId: correlationId);
        var dispatchedAt = Stopwatch.StartNew();
        try
        {
            distributedCoordinator.Queue(request, allowRemote: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(policy.MaximumJobDuration);
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var job = distributedCoordinator.ListJobs().SingleOrDefault(item => item.Request.JobId == jobId);
                if (job?.State == DistributedJobStateV1.Completed && job.Result is { } result)
                {
                    var handle = result.GetProperty("handle").GetString();
                    var declaredLength = result.GetProperty("length").GetInt64();
                    var expectedHash = result.GetProperty("sha256").GetString();
                    if (handle is null || expectedHash is null || declaredLength <= 0 || declaredLength > grant.MaximumResultBytes)
                    {
                        return null;
                    }
                    await using var published = await mediaTransfers.OpenPublishedResultAsync(handle, timeout.Token).ConfigureAwait(false);
                    if (published is null)
                    {
                        return null;
                    }
                    var partial = outputPath + "." + Guid.NewGuid().ToString("N") + ".partial.mp4";
                    try
                    {
                        long total = 0;
                        byte[] actualHash;
                        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            var buffer = new byte[64 * 1024];
                            while (true)
                            {
                                var read = await published.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                total += read;
                                if (total > declaredLength || total > grant.MaximumResultBytes) throw new InvalidDataException("Remote result exceeded its authorized length.");
                                hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                            }
                            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                            actualHash = hash.GetHashAndReset();
                        }
                        if (total != declaredLength || !CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash))) throw new InvalidDataException("Remote result integrity check failed.");
                        await using (var verify = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.Asynchronous))
                        {
                            var header = new byte[8]; if (await verify.ReadAsync(header, timeout.Token).ConfigureAwait(false) != header.Length || System.Text.Encoding.ASCII.GetString(header, 4, 4) != "ftyp") throw new InvalidDataException("Remote output is not a valid MP4 container.");
                        }
                        File.Move(partial, outputPath);
                        PruneCache(outputPath);
                        var ready = TryReady(cacheKey, outputPath);
                        return ready;
                    }
                    finally { try { File.Delete(partial); } catch (IOException) { } }
                }
                if (job is null || job.State is DistributedJobStateV1.Failed or DistributedJobStateV1.Cancelled or DistributedJobStateV1.Uncertain) return null;
                if (job.State == DistributedJobStateV1.Pending && dispatchedAt.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    distributedCoordinator.CancelJob(jobId);
                    return null;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            distributedCoordinator.CancelJob(jobId);
            mediaTransfers.Revoke(jobId);
            throw;
        }
        catch (OperationCanceledException)
        {
            distributedCoordinator.CancelJob(jobId);
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or HttpRequestException)
        {
            distributedCoordinator.CancelJob(jobId);
            return null;
        }
        finally { mediaTransfers.Revoke(jobId); }
    }

    public ValueTask<ReadableMediaContent?> OpenReadAsync(TranscodedContentHandle content, CancellationToken cancellationToken = default)
    {
        if (!IsValidCacheKey(content.Value)) return ValueTask.FromResult<ReadableMediaContent?>(null);
        var path = GetCachePath(content.Value);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return ValueTask.FromResult<ReadableMediaContent?>(new ReadableMediaContent(stream, new SourceContentFacts(stream.Length, File.GetLastWriteTimeUtc(path))));
        }
        catch (FileNotFoundException) { return ValueTask.FromResult<ReadableMediaContent?>(null); }
        catch (DirectoryNotFoundException) { return ValueTask.FromResult<ReadableMediaContent?>(null); }
    }

    private async Task<bool> RunWorkerAsync(string key, string codec, Stream source, long sourceLength, string correlationId, CancellationToken cancellationToken)
    {
        if (!File.Exists(workerAssemblyPath)) throw new FileNotFoundException("FFmpeg worker assembly is missing.", workerAssemblyPath);
        var request = new FfmpegWorkerRequestV1(FfmpegWorkerRequestV1.ContractId, key, codec, sourceLength) { CorrelationId = correlationId };
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(workerAssemblyPath);
        start.ArgumentList.Add("--protocol-v1");
        start.Environment["HISPANIA_FFMPEG_PATH"] = ffmpegPath;
        start.Environment["HISPANIA_FFMPEG_CACHE"] = cacheDirectory;
        start.Environment["HISPANIA_FFMPEG_REQUEST_V1"] = JsonSerializer.Serialize(request);
        start.Environment["HISPANIA_FFMPEG_MAX_OUTPUT_BYTES"] = policy.MaximumOutputBytesPerJob.ToString(System.Globalization.CultureInfo.InvariantCulture);
        policy.ConfigureStartInfo(start);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) return false;
        IDisposable? job;
        try { job = FfmpegWorkerPolicy.AttachWindowsJob(process, policy); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            CleanupPartialFiles(key);
            throw;
        }
        using var windowsJob = job;
        var resourceSampleTask = ObserveProcessResourcesAsync(process);
        var stdoutTask = ReadBoundedResponseAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderrTask = DrainBoundedTextAsync(process.StandardError.BaseStream, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(policy.MaximumJobDuration);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await source.CopyToAsync(process.StandardInput.BaseStream, timeout.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await resourceSampleTask.ConfigureAwait(false);
            CleanupPartialFiles(key);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("FFmpeg worker exceeded its configured job duration.", exception);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await resourceSampleTask.ConfigureAwait(false);
            CleanupPartialFiles(key);
            throw;
        }
        await stderrTask.ConfigureAwait(false);
        FfmpegWorkerResponseV1? response;
        try { response = JsonSerializer.Deserialize<FfmpegWorkerResponseV1>(await stdoutTask.ConfigureAwait(false)); }
        catch { CleanupPartialFiles(key); throw; }
        var resourceSample = await resourceSampleTask.ConfigureAwait(false);
        PeakWorkingSet.Record(resourceSample.PeakWorkingSetBytes);
        CpuTime.Record(resourceSample.CpuTimeMilliseconds);
        var succeeded = process.ExitCode == 0 && response is { ProtocolVersion: 1, Success: true, ContentKey: not null } && response.ContentKey == key &&
            (response.CorrelationId is null || response.CorrelationId == correlationId);
        if (!succeeded) CleanupPartialFiles(key);
        return succeeded;
    }

    private static async Task<string> ReadBoundedResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int maximumResponseBytes = 4096;
        var bytes = new byte[maximumResponseBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length, bytes.Length - length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumResponseBytes) throw new InvalidDataException("FFmpeg worker response exceeded its protocol limit.");
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static async Task<string> DrainBoundedTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int maximumCapturedBytes = 4096;
        using var captured = new MemoryStream(maximumCapturedBytes);
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var keep = Math.Min(read, maximumCapturedBytes - (int)captured.Length);
            if (keep > 0) captured.Write(buffer, 0, keep);
        }
        return Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length);
    }

    private static async Task<WorkerResourceSample> ObserveProcessResourcesAsync(Process process)
    {
        long peakWorkingSetBytes = 0;
        while (true)
        {
            try
            {
                if (!process.HasExited) peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
                else break;
            }
            catch (InvalidOperationException) { break; }
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        double cpuTimeMilliseconds;
        try { cpuTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds; }
        catch (InvalidOperationException) { cpuTimeMilliseconds = 0; }
        return new WorkerResourceSample(peakWorkingSetBytes, cpuTimeMilliseconds);
    }

    private sealed record WorkerResourceSample(long PeakWorkingSetBytes, double CpuTimeMilliseconds);

    private static bool IsSupportedSourceExtension(string extension) => extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    private string GetCachePath(string key) => Path.Combine(cacheDirectory, key + ".mp4");
    private static bool IsValidCacheKey(string key) => key.Length == 64 && key.All(Uri.IsHexDigit);
    private void CleanupPartialFiles(string key)
    {
        try
        {
            foreach (var file in new DirectoryInfo(cacheDirectory).EnumerateFiles(key + ".*.partial.mp4"))
            {
                try { file.Delete(); } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static CompatibleTranscodeReady? TryReady(string key, string path)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        return info.Length > 0 ? new CompatibleTranscodeReady(TranscodedContentHandle.FromCacheKey(key), "video/mp4", info.Length) : null;
    }

    private void PruneCache(string newestFile)
    {
        try
        {
            var files = new DirectoryInfo(cacheDirectory).EnumerateFiles("*.mp4")
                .Where(file => !file.Name.Contains(".partial.", StringComparison.Ordinal))
                .OrderByDescending(file => file.LastAccessTimeUtc).ToArray();
            var total = files.Sum(file => file.Length);
            foreach (var old in files.Where(file => !string.Equals(file.FullName, newestFile, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Reverse())
            {
                if (total <= policy.MaximumCacheBytes) break;
                try { total -= old.Length; old.Delete(); } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RemoveStalePartialFiles()
    {
        try
        {
            var staleBefore = DateTime.UtcNow - policy.MaximumJobDuration - TimeSpan.FromHours(1);
            foreach (var file in new DirectoryInfo(cacheDirectory).EnumerateFiles("*.partial.mp4"))
            {
                if (file.LastWriteTimeUtc < staleBefore)
                {
                    try { file.Delete(); } catch (IOException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
