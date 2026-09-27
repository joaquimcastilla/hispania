using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Transcoding.Worker.Contracts;

namespace Hispania.Transcoding.FFmpeg;

/// <summary>Remote capability adapter that reuses the existing FFmpeg worker protocol and resource policy.</summary>
public sealed class DistributedFfmpegCapabilityHandler : IDistributedCapabilityHandlerV1, IDisposable
{
    private const int TransferBlockBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly string nodeId;
    private readonly string workerAssemblyPath;
    private readonly string ffmpegPath;
    private readonly string cacheDirectory;
    private readonly FfmpegWorkerPolicy policy;
    private readonly FileStream cacheLease;

    public DistributedFfmpegCapabilityHandler(Uri coordinatorUri, string nodeId, X509Certificate2 certificate,
        IReadOnlyCollection<string> serverPins, string workerAssemblyPath, string ffmpegPath, string cacheDirectory, FfmpegWorkerPolicy policy)
    {
        this.nodeId = nodeId; this.workerAssemblyPath = Path.GetFullPath(workerAssemblyPath); this.ffmpegPath = ffmpegPath;
        this.cacheDirectory = Path.GetFullPath(cacheDirectory); this.policy = policy; policy.Validate();
        ValidateSoftwareFfmpeg(this.workerAssemblyPath, this.ffmpegPath);
        Directory.CreateDirectory(this.cacheDirectory);
        cacheLease = new FileStream(Path.Combine(this.cacheDirectory, ".distributed-ffmpeg-worker.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        // This directory is private to one worker process. Once its exclusive lock is held, any
        // leftovers can only belong to work interrupted by a prior process termination.
        foreach (var stale in Directory.EnumerateFiles(this.cacheDirectory, "*.source.partial").Concat(Directory.EnumerateFiles(this.cacheDirectory, "*.mp4")))
            TryDelete(stale);
        if (coordinatorUri.Scheme != Uri.UriSchemeHttps || serverPins.Count == 0) throw new ArgumentException("Remote FFmpeg requires HTTPS and server pins.");
        var handler = new HttpClientHandler { ClientCertificateOptions = ClientCertificateOption.Manual };
        handler.ClientCertificates.Add(certificate);
        var normalizedPins = serverPins.Select(static pin => pin.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        handler.ServerCertificateCustomValidationCallback = (_, server, _, _) => server is not null &&
            server.NotBefore.ToUniversalTime() <= DateTime.UtcNow && server.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
            normalizedPins.Contains(Convert.ToHexString(SHA256.HashData(server.RawData)));
        client = new HttpClient(handler) { BaseAddress = new Uri(coordinatorUri, "/internal/worker/v1/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public IReadOnlyCollection<DistributedCapabilityV1> Capabilities { get; } = [new(DistributedFfmpegJobV1.ContractId, DistributedFfmpegJobV1.ContractVersion)];

    public async ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken)
    {
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(policy.MaximumJobDuration);
        var jobToken = overallTimeout.Token;
        if (request.Operation != "Transcode") throw new InvalidOperationException("Unsupported remote FFmpeg operation.");
        var job = request.Payload.Deserialize<DistributedFfmpegJobV1>(JsonOptions);
        var perJobTemporaryBytes = policy.MaximumTemporaryStorageBytes / policy.MaximumConcurrentJobs;
        if (job is null || !job.IsValid || job.SourceLength > perJobTemporaryBytes / 2 || job.MaximumOutputBytes > perJobTemporaryBytes / 2)
            throw new InvalidDataException("Invalid remote FFmpeg job payload.");
        using var grantResponse = await client.GetAsync(TransferUri(request.JobId, "media/grant", job.TransferId, $"generation={generation}"), jobToken).ConfigureAwait(false);
        grantResponse.EnsureSuccessStatusCode();
        var grant = await grantResponse.Content.ReadFromJsonAsync<DistributedMediaTransferGrantV1>(JsonOptions, jobToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Coordinator did not issue the leased media grant.");
        if (grant.TransferId != job.TransferId || grant.SourceLength != job.SourceLength || grant.MaximumResultBytes != job.MaximumOutputBytes || grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Coordinator media grant does not match this FFmpeg job.");
        Directory.CreateDirectory(cacheDirectory);
        var inputPath = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".source.partial");
        var outputPath = Path.Combine(cacheDirectory, job.CacheKey + ".mp4");
        try
        {
            await DownloadSourceAsync(request.JobId, generation, job, grant.AccessToken, inputPath, jobToken).ConfigureAwait(false);
            if (!await RunWorkerAsync(job, inputPath, jobToken).ConfigureAwait(false)) throw new InvalidOperationException("ffmpeg_transcode_failed");
            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length is <= 0 || info.Length > job.MaximumOutputBytes) throw new InvalidDataException("Remote FFmpeg output is missing or over its limit.");
            await using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, jobToken).ConfigureAwait(false));
            output.Position = 0;
            var uri = TransferUri(request.JobId, "media/result", job.TransferId,
                $"generation={generation}&length={info.Length}&sha256={Uri.EscapeDataString(hash)}");
            using var content = new StreamContent(output, 64 * 1024);
            content.Headers.ContentLength = info.Length;
            using var message = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, jobToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict) throw new OperationCanceledException("Remote FFmpeg lease was fenced.", cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: jobToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(inputPath); TryDelete(outputPath);
        }
    }

    private async Task DownloadSourceAsync(string jobId, long generation, DistributedFfmpegJobV1 job, string accessToken, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        for (long offset = 0; offset < job.SourceLength;)
        {
            var count = (int)Math.Min(TransferBlockBytes, job.SourceLength - offset);
            var uri = TransferUri(jobId, "media/source", job.TransferId, $"generation={generation}&offset={offset}&length={count}");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } actualLength && actualLength != count) throw new EndOfStreamException("Coordinator returned an incorrect media block length.");
            await using var block = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var copied = await CopyExactlyAsync(block, output, count, cancellationToken).ConfigureAwait(false);
            if (copied != count) throw new EndOfStreamException("Media source transfer was truncated.");
            offset += count;
        }
    }

    private async Task<bool> RunWorkerAsync(DistributedFfmpegJobV1 job, string sourcePath, CancellationToken cancellationToken)
    {
        var request = new FfmpegWorkerRequestV1(FfmpegWorkerRequestV1.ContractId, job.CacheKey, job.VideoCodec, job.SourceLength) { CorrelationId = job.CorrelationId };
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(workerAssemblyPath); start.ArgumentList.Add("--protocol-v1");
        start.Environment["HISPANIA_FFMPEG_PATH"] = ffmpegPath; start.Environment["HISPANIA_FFMPEG_CACHE"] = cacheDirectory;
        start.Environment["HISPANIA_FFMPEG_REQUEST_V1"] = JsonSerializer.Serialize(request);
        start.Environment["HISPANIA_FFMPEG_MAX_OUTPUT_BYTES"] = job.MaximumOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        policy.ConfigureStartInfo(start);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) return false;
        using var processJob = FfmpegWorkerPolicy.AttachWindowsJob(process, policy);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(policy.MaximumJobDuration);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(process.StandardInput.BaseStream, timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw;
        }
        await stderr.ConfigureAwait(false);
        if (await stdout.ConfigureAwait(false) is not { Length: <= 4096 } responseText) return false;
        var response = JsonSerializer.Deserialize<FfmpegWorkerResponseV1>(responseText);
        return process.ExitCode == 0 && response is { ProtocolVersion: 1, Success: true } && response.ContentKey == job.CacheKey;
    }

    private string TransferUri(string jobId, string suffix, string transferId, string query) =>
        $"{Uri.EscapeDataString(nodeId)}/jobs/{Uri.EscapeDataString(jobId)}/{suffix}?transferId={Uri.EscapeDataString(transferId)}&{query}";
    private static async Task<int> CopyExactlyAsync(Stream input, Stream output, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024]; var total = 0;
        while (total < length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length - total)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break; await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); total += read;
        }
        if (total == length && await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0) throw new InvalidDataException("Coordinator media block exceeded its declared length.");
        return total;
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void Dispose() { client.Dispose(); cacheLease.Dispose(); }

    private static void ValidateSoftwareFfmpeg(string workerPath, string executable)
    {
        if (!File.Exists(workerPath)) throw new FileNotFoundException("The packaged FFmpeg worker assembly is missing.", workerPath);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-hide_banner"); start.ArgumentList.Add("-encoders");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not be started on this worker.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("FFmpeg capability discovery exceeded 10 seconds.");
        }
        var listing = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !listing.Contains("libx264", StringComparison.Ordinal))
            throw new InvalidOperationException("The remote FFmpeg capability requires the software libx264 encoder.");
    }
}
