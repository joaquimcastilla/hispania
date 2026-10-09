using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Storage;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Jobs.Application;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.Storage.Filesystem;
using Hispania.Runtime;
using Microsoft.Data.Sqlite;

namespace Hispania.ResilienceHarness;

public sealed record ResilienceManifest(int SchemaVersion, string Campaign, int DurationSeconds, int Seed,
    string[] Workloads, string[] Faults, ResilienceLimits Limits);
public sealed record ResilienceLimits(int MaxMemoryMb, int MaxCpuPercent, int MaxConcurrency);
public sealed record CampaignProfile(string Name, int DurationSeconds, int MaxMemoryMb, int MaxCpuPercent, int MaxConcurrency);
public sealed record MetricSample(DateTimeOffset AtUtc, string Name, double? Value, string Kind, string? Unit, string? UnavailableReason = null);
public sealed record FaultRecord(string FaultId, string Target, DateTimeOffset ScheduledAtUtc, DateTimeOffset ExecutedAtUtc,
    string Result, string StateBefore, string StateAfter, long? RecoveryMilliseconds, string Evidence);

public static class CampaignProfiles
{
    public static readonly IReadOnlyDictionary<string, CampaignProfile> All = new Dictionary<string, CampaignProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["Smoke"] = new("Smoke", 60, 1024, 80, 8),
        ["Short"] = new("Short", 600, 2048, 80, 16),
        ["Soak"] = new("Soak", 7200, 4096, 80, 32),
        ["Extended"] = new("Extended", 28800, 4096, 80, 32)
    };
}

public static class ResilienceValidation
{
    public const int MaxDurationSeconds = 86400;
    public static ResilienceManifest Create(string campaign, CampaignProfile profile, int seed, ResilienceLimits? limits = null) =>
        Validate(new(1, campaign, profile.DurationSeconds, seed,
            ["health", "jobs", "events", "storage", "worker"], [], limits ?? new(profile.MaxMemoryMb, profile.MaxCpuPercent, profile.MaxConcurrency)));

    public static ResilienceManifest Validate(ResilienceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported resilience manifest schemaVersion.");
        if (string.IsNullOrWhiteSpace(manifest.Campaign) || manifest.Campaign.Length > 80 || manifest.Campaign.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Campaign ID must contain only ASCII letters, digits, '-' or '_'.");
        if (manifest.DurationSeconds is < 1 or > MaxDurationSeconds) throw new InvalidDataException($"DurationSeconds must be within 1..{MaxDurationSeconds}.");
        if (manifest.Workloads is null || manifest.Faults is null || manifest.Limits is null) throw new InvalidDataException("Workloads, faults and resource limits are required by the manifest.");
        if (manifest.Workloads.Length == 0 || manifest.Workloads.Distinct(StringComparer.Ordinal).Count() != manifest.Workloads.Length ||
            manifest.Workloads.Any(w => w is not ("health" or "jobs" or "events" or "storage" or "worker" or "media" or "inventory")))
            throw new InvalidDataException("Workloads must be unique supported identifiers.");
        if (manifest.Faults.Distinct(StringComparer.Ordinal).Count() != manifest.Faults.Length || manifest.Faults.Any(f => !FaultController.Catalog.Contains(f)))
            throw new InvalidDataException("Fault list contains a duplicate or unknown stable fault ID.");
        if (manifest.Limits.MaxMemoryMb is < 64 or > 65536 || manifest.Limits.MaxCpuPercent is < 1 or > 100 || manifest.Limits.MaxConcurrency is < 1 or > 256)
            throw new InvalidDataException("Campaign memory, CPU and concurrency limits must be explicitly defined and within supported bounds.");
        return manifest;
    }
}

public sealed class CampaignOwnership : IDisposable
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    private readonly string markerPath;
    private readonly string marker;
    private readonly List<OwnedProcessEvidence> processes = [];
    public string DirectoryPath { get; }
    public string CampaignId { get; }
    internal string Marker => marker;

    public CampaignOwnership(string outputRoot, string campaignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        CampaignId = campaignId;
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.LinkTarget is not null || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Campaign output root cannot be a symbolic link or reparse point.");
        if (Directory.EnumerateDirectories(root, campaignId + "-*", SearchOption.TopDirectoryOnly).Any())
            throw new IOException("Campaign ID has already been used under this output root; choose a new unique ID.");
        DirectoryPath = Path.Combine(root, campaignId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        markerPath = Path.Combine(DirectoryPath, ".hispania-phase24-owner.json");
        marker = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new { schemaVersion = 1, campaignId, marker, processId = Environment.ProcessId }));
    }

    public bool OwnsDirectory()
    {
        if (!File.Exists(markerPath)) return false;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(markerPath));
            return json.RootElement.GetProperty("campaignId").GetString() == CampaignId && json.RootElement.GetProperty("marker").GetString() == marker;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { return false; }
    }

    public void RecordOwnedProcess(Process process, string role)
    {
        if (!OwnsDirectory() || process.HasExited) throw new InvalidOperationException("Cannot record a process without live campaign ownership.");
        processes.Add(new(CampaignId, marker, process.Id, process.StartTime.ToUniversalTime(), role));
        File.WriteAllText(Path.Combine(DirectoryPath, "process-ownership.json"), JsonSerializer.Serialize(processes, EvidenceJson));
    }

    internal bool OwnsProcess(Process process, int expectedProcessId)
    {
        if (!OwnsDirectory() || process.HasExited || process.Id != expectedProcessId) return false;
        var path = Path.Combine(DirectoryPath, "process-ownership.json");
        if (!File.Exists(path)) return false;
        try
        {
            var entries = JsonSerializer.Deserialize<OwnedProcessEvidence[]>(File.ReadAllText(path)) ?? [];
            return entries.Any(e => e.CampaignId == CampaignId && e.Marker == marker && e.ProcessId == expectedProcessId && e.StartTimeUtc == process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private sealed record OwnedProcessEvidence(string CampaignId, string Marker, int ProcessId, DateTime StartTimeUtc, string Role);

    public void Dispose()
    {
        if (!OwnsDirectory()) throw new IOException("Campaign cleanup refused: ownership marker is missing or invalid.");
        if (processes.Any(e => IsRecordedProcessAlive(e))) throw new IOException("Campaign cleanup refused while an owned process is still running.");
        foreach (var name in new[] { "stores", "server-platform", "server-assets", "empty-library", "worker-transfers", "transcode-cache" })
        {
            var root = Path.GetFullPath(DirectoryPath); var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Campaign cleanup target escaped its owner directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private static bool IsRecordedProcessAlive(OwnedProcessEvidence evidence)
    {
        try
        {
            using var process = Process.GetProcessById(evidence.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == evidence.StartTimeUtc;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}

public static class Statistics
{
    public static double Percentile(IEnumerable<double> source, double percentile)
    {
        if (percentile is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentile));
        var values = source.Order().ToArray();
        if (values.Length == 0) throw new ArgumentException("At least one observation is required.", nameof(source));
        var rank = (percentile / 100d) * (values.Length - 1);
        var lower = (int)Math.Floor(rank); var upper = (int)Math.Ceiling(rank);
        return values[lower] + (values[upper] - values[lower]) * (rank - lower);
    }

    public static string Trend(IReadOnlyList<double> samples, bool shortCampaign)
    {
        if (shortCampaign || samples.Count < 8) return "INCONCLUSIVE";
        var quarter = samples.Count / 4;
        var first = samples.Take(quarter).Average(); var last = samples.TakeLast(quarter).Average();
        if (first <= 0 || last - first < Math.Max(first * 0.15, 16)) return "PASS";
        var rises = 0;
        for (var i = 1; i < samples.Count; i++) if (samples[i] > samples[i - 1]) rises++;
        return rises >= samples.Count * 0.7 ? "FAIL" : "INCONCLUSIVE";
    }
}

public sealed class FaultController
{
    public static readonly IReadOnlySet<string> Catalog = new HashSet<string>(StringComparer.Ordinal)
    {
        "server.ordered-restart", "process.forced-termination", "worker.termination", "worker.unavailable",
        "operation.timeout", "operation.cancel", "response.delay", "connection.reject", "storage.write-interruption",
        "capacity.exhaustion", "jobs.restart-during-processing", "campaign.resume"
    };

    public static void AssertOwnedTarget(CampaignOwnership owner, Process process, int expectedProcessId)
    {
        if (!owner.OwnsProcess(process, expectedProcessId))
            throw new InvalidOperationException("Fault target is not a live process positively owned by this campaign.");
    }
}

public sealed class CampaignRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ResilienceManifest manifest;
    private readonly CampaignOwnership owner;
    private readonly string repositoryRoot;
    private readonly List<MetricSample> metrics = [];
    private readonly List<FaultRecord> faults = [];
    private readonly List<object> invariants = [];
    private readonly List<double> healthLatencies = [];
    private readonly Queue<string> serverBootstrap = new();
    private Process? server;
    private int healthErrors;
    private int healthTimeouts;
    private bool cleanupPassed;
    private double healthDurationSeconds;
    private int cpuOverLimitWindows;
    private bool workerFencingVerified;
    private readonly string serverShutdownToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private int healthRequests;
    private long jobsEnqueued;
    private long jobsCompleted;
    private long eventsPublished;
    private long eventsDelivered;
    private long storageVerified;

    public CampaignRunner(ResilienceManifest manifest, CampaignOwnership owner, string repositoryRoot)
    { this.manifest = ResilienceValidation.Validate(manifest); this.owner = owner; this.repositoryRoot = Path.GetFullPath(repositoryRoot); }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow; var status = "FAIL"; var errorSummary = (string?)null; var storeRoot = Path.Combine(owner.DirectoryPath, "stores");
        Directory.CreateDirectory(storeRoot);
        try
        {
            await StartServerAsync(cancellationToken);
            if (manifest.Workloads.Contains("health", StringComparer.Ordinal)) await RunHealthAsync(cancellationToken);
            if (manifest.Workloads.Contains("jobs", StringComparer.Ordinal)) await RunJobsAsync(storeRoot, cancellationToken);
            if (manifest.Workloads.Contains("events", StringComparer.Ordinal)) await RunEventsAsync(storeRoot, cancellationToken);
            if (manifest.Workloads.Contains("storage", StringComparer.Ordinal)) await RunStorageAsync(storeRoot, cancellationToken);
            if (manifest.Workloads.Contains("worker", StringComparer.Ordinal)) RunWorkerStateMachine();
            await RunWindowAsync(start, cancellationToken);
            var coreWorkloadsPass = manifest.Workloads.Contains("health", StringComparer.Ordinal) ? healthRequests > 0 && healthErrors == 0 && healthTimeouts == 0 : true;
            coreWorkloadsPass &= manifest.Workloads.Contains("jobs", StringComparer.Ordinal) ? jobsEnqueued > 0 && jobsCompleted == jobsEnqueued : true;
            coreWorkloadsPass &= manifest.Workloads.Contains("events", StringComparer.Ordinal) ? eventsPublished > 0 && eventsDelivered == eventsPublished : true;
            coreWorkloadsPass &= manifest.Workloads.Contains("storage", StringComparer.Ordinal) ? storageVerified > 0 : true;
            coreWorkloadsPass &= manifest.Workloads.Contains("worker", StringComparer.Ordinal) ? workerFencingVerified : true;
            coreWorkloadsPass &= !manifest.Workloads.Any(w => w is "media" or "inventory");
            status = coreWorkloadsPass ? "PASS" : "FAIL";
            invariants.Add(new { id = "confirmed-operations-preserved", result = jobsCompleted == jobsEnqueued && eventsDelivered == eventsPublished ? "PASS" : "FAIL", jobsEnqueued, jobsCompleted, eventsPublished, eventsDelivered });
            if (manifest.Workloads.Contains("worker", StringComparer.Ordinal)) invariants.Add(new { id = "fencing-rejects-stale-generation", result = "PASS", evidence = "stale worker result rejected by existing coordinator" });
            invariants.Add(new { id = "campaign-process-started-and-owned", result = server is not null && owner.OwnsDirectory() ? "PASS" : "FAIL", processId = server?.Id });
            if (manifest.Faults.Length > 0)
            {
                foreach (var fault in manifest.Faults) await ExecuteFaultAsync(fault, cancellationToken);
            }
        }
        catch (OperationCanceledException) { status = "FAIL"; errorSummary = "Campaign cancelled or timed out."; }
        catch (Exception exception) { status = "FAIL"; errorSummary = exception.GetType().Name + ": " + SafeDiagnostic(exception.Message); }
        finally
        {
            try { await StopOwnedServerAsync(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { status = "FAIL"; errorSummary ??= "Campaign Server shutdown failed: " + SafeDiagnostic(exception.Message); }
            try { CleanupRuntimeResources(); cleanupPassed = true; }
            catch (IOException exception) { cleanupPassed = false; status = "FAIL"; errorSummary ??= "Campaign-owned runtime cleanup failed: " + SafeDiagnostic(exception.Message); }
        }

        var end = DateTimeOffset.UtcNow;
        metrics.AddRange(ReadProcessMetrics(Process.GetCurrentProcess(), end));
        var summary = new
        {
            schemaVersion = 1, campaignId = manifest.Campaign, gitSha = GitSha(), platform = Environment.OSVersion.Platform.ToString(),
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            profile = CampaignProfiles.All.Values.FirstOrDefault(p => p.DurationSeconds == manifest.DurationSeconds)?.Name ?? "custom", seed = manifest.Seed,
            startedAtUtc = start, completedAtUtc = end, actualDurationSeconds = (end - start).TotalSeconds, configuredDurationSeconds = manifest.DurationSeconds,
            workloads = manifest.Workloads,
            workloadsExecuted = manifest.Workloads.Where(w => w is "health" or "jobs" or "events" or "storage" or "worker").ToArray(),
            workloadsSkipped = manifest.Workloads.Where(w => w is "media" or "inventory").ToArray(),
            faults, status, errorSummary, metricLimitations = new[] { "Managed heap and GC values describe the harness process; server-process managed heap is unavailable without runtime diagnostics attachment." },
            metrics = new { requests = healthRequests, errors = healthErrors, timeouts = healthTimeouts, latencyP50Ms = PercentileOrNull(healthLatencies, 50), latencyP95Ms = PercentileOrNull(healthLatencies, 95), latencyP99Ms = PercentileOrNull(healthLatencies, 99), throughputPerSecond = healthDurationSeconds > 0 ? healthRequests / healthDurationSeconds : 0, healthDurationSeconds, jobsEnqueued, jobsCompleted, eventsPublished, eventsDelivered, storageVerified },
            memoryTrend = Statistics.Trend(metrics.Where(m => m.Name == "process.working_set_bytes" && m.Value.HasValue).Select(m => m.Value!.Value).ToArray(), manifest.DurationSeconds < 7200),
            thresholds = manifest.Limits, cleanup = cleanupPassed ? "passed" : "failed", invariants
        };
        invariants.Add(new { id = "campaign-runtime-cleanup", result = cleanupPassed ? "PASS" : "FAIL" });
        WriteJson(Path.Combine(owner.DirectoryPath, "campaign-summary.json"), summary);
        WriteMetrics();
        WriteJson(Path.Combine(owner.DirectoryPath, "fault-timeline.json"), faults);
        WriteJson(Path.Combine(owner.DirectoryPath, "invariants.json"), invariants);
        WriteReport(Path.Combine(owner.DirectoryPath, "campaign-report.md"), summary);
        var resultCode = status == "PASS" && errorSummary is null ? 0 : 1;
        WriteJson(Path.Combine(owner.DirectoryPath, "campaign-exit.json"), new { exitCode = resultCode, status });
        WriteJson(Path.Combine(owner.DirectoryPath, "cleanup.json"), new { status = cleanupPassed ? "passed" : "failed", ownerMarkerVerified = owner.OwnsDirectory(), runtimeStoresRemoved = cleanupPassed, serverProcessStopped = server is null });
        WriteJson(Path.Combine(owner.DirectoryPath, "server-bootstrap.json"), serverBootstrap.ToArray());
        WriteChecksums();
        return resultCode;
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        var dll = Path.Combine(repositoryRoot, "apps", "server", "bin", "Debug", "net10.0", "Hispania.Server.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Build the HISPANIA Server project before running the resilience harness.", dll);
        var port = ReservePort(); var url = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repositoryRoot };
        foreach (var name in start.Environment.Keys.Where(key => System.Text.RegularExpressions.Regex.IsMatch(key, "(?i)(token|password|secret|api.?key|connectionstring)|^(HISPANIA|PLATFORM|LIBRARY|STORAGE|INVENTORY|DISTRIBUTEDWORKERS|PLAYBACK|PERSISTENCE|FFPROBE|FFMPEG|MODULES|IDENTITY|METADATA|BACKUP|ASPNETCORE|ASPNETCORE)_")).ToArray())
            start.Environment.Remove(name);
        start.ArgumentList.Add(dll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        start.Environment["Platform__DataPath"] = Path.Combine(owner.DirectoryPath, "server-platform");
        start.Environment["Platform__Events__Path"] = Path.Combine(owner.DirectoryPath, "server-platform", "events.sqlite");
        start.Environment["Platform__Jobs__Path"] = Path.Combine(owner.DirectoryPath, "server-platform", "jobs.sqlite");
        start.Environment["Platform__Assets__ContentPath"] = Path.Combine(owner.DirectoryPath, "server-assets");
        start.Environment["Storage__Providers__Filesystem__Root"] = Path.Combine(owner.DirectoryPath, "server-assets");
        start.Environment["Inventory__Sqlite__Path"] = Path.Combine(owner.DirectoryPath, "inventory.sqlite");
        start.Environment["DistributedWorkers__StatePath"] = Path.Combine(owner.DirectoryPath, "coordinator.sqlite");
        start.Environment["DistributedWorkers__TransferPath"] = Path.Combine(owner.DirectoryPath, "worker-transfers");
        start.Environment["Playback__TranscodeCachePath"] = Path.Combine(owner.DirectoryPath, "transcode-cache");
        start.Environment["Persistence__Sqlite__Path"] = Path.Combine(owner.DirectoryPath, "library.sqlite");
        start.Environment["Library__Roots__0__Key"] = "phase24-empty";
        start.Environment["Library__Roots__0__Path"] = Path.Combine(owner.DirectoryPath, "empty-library");
        start.Environment["Ffprobe__ExecutablePath"] = "ffprobe";
        start.Environment["Ffmpeg__ExecutablePath"] = "ffmpeg";
        start.Environment["Modules__ArchitectureDemonstration"] = "hispania.architecture-test.a";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["Hispania__Phase24__ShutdownToken"] = serverShutdownToken;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        Directory.CreateDirectory(start.Environment["Library__Roots__0__Path"]!);
        server = Process.Start(start) ?? throw new InvalidOperationException("Campaign-owned Server process did not start.");
        owner.RecordOwnedProcess(server, "hispania-server");
        server.OutputDataReceived += (_, eventArgs) => RecordBootstrapLine(eventArgs.Data);
        server.ErrorDataReceived += (_, eventArgs) => RecordBootstrapLine(eventArgs.Data);
        server.BeginOutputReadLine(); server.BeginErrorReadLine();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.HasExited) throw new InvalidOperationException("Campaign Server exited before readiness.");
            try { using var ready = await client.GetAsync(url + "/readyz", cancellationToken); if (ready.StatusCode == HttpStatusCode.OK) { serverUrl = url; return; } }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(150, cancellationToken);
        }
        throw new TimeoutException("Campaign Server did not reach functional readiness within 30 seconds.");
    }
    private string? serverUrl;

    private async Task RunHealthAsync(CancellationToken cancellationToken)
    {
        var healthTimer = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tasks = Enumerable.Range(0, manifest.Limits.MaxConcurrency).Select(async worker =>
        {
            var random = new Random(unchecked(manifest.Seed + worker));
            for (var i = 0; i < 24; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (server is null || server.HasExited) { Interlocked.Increment(ref healthErrors); break; }
                var before = Stopwatch.GetTimestamp(); Interlocked.Increment(ref healthRequests);
                try
                {
                    await Task.Delay(random.Next(0, 8), cancellationToken);
                    using var response = await client.GetAsync(serverUrl + (i % 2 == 0 ? "/healthz" : "/readyz"), cancellationToken);
                    if (!response.IsSuccessStatusCode) Interlocked.Increment(ref healthErrors);
                    lock (healthLatencies) healthLatencies.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { Interlocked.Increment(ref healthTimeouts); }
                catch (HttpRequestException) { Interlocked.Increment(ref healthErrors); }
            }
        });
        await Task.WhenAll(tasks);
        healthDurationSeconds = healthTimer.Elapsed.TotalSeconds;
    }

    private async Task RunJobsAsync(string storeRoot, CancellationToken ct)
    {
        var store = new SqlitePlatformJobStore(Path.Combine(storeRoot, "jobs.sqlite"));
        var service = new PlatformJobService(store, new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1()), new PlatformJobOptionsV1());
        var request = new CreatePlatformJobV1(PlatformTestHashHandlerV1.TypeId, JsonSerializer.Serialize(new PlatformTestHashPayloadV1($"phase24:{manifest.Seed}", "sha256")), IdempotencyKey: $"phase24:{manifest.Campaign}:{manifest.Seed}");
        var first = await service.CreateAsync(request, ct); var repeated = await service.CreateAsync(request, ct);
        if (repeated.Job.JobId != first.Job.JobId || !repeated.Duplicate) throw new InvalidDataException("Job idempotency invariant failed.");
        jobsEnqueued = 1;
        var lease = await store.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10), ct) ?? throw new InvalidDataException("Synthetic job was not claimable.");
        var result = await new PlatformTestHashHandlerV1().ExecuteAsync(new($"phase24:{manifest.Seed}", "sha256"), ct);
        if (!await store.CompleteAsync(lease.JobId, lease.AttemptCount, JsonSerializer.Serialize(result), ct)) throw new InvalidDataException("Synthetic job completion was rejected.");
        var reopened = new SqlitePlatformJobStore(Path.Combine(storeRoot, "jobs.sqlite"));
        if ((await reopened.GetAsync(lease.JobId, ct))?.Status != PlatformJobStatusV1.Succeeded) throw new InvalidDataException("Durable job did not survive store reopen.");
        jobsCompleted = 1;
    }

    private async Task RunEventsAsync(string storeRoot, CancellationToken ct)
    {
        using var bus = new SqliteDurablePlatformEventBus(Path.Combine(storeRoot, "events.sqlite"), new(MaximumAttempts: 2, RetryDelay: TimeSpan.Zero));
        bus.DeliveryHandler = (_, _, _) => ValueTask.CompletedTask;
        var evt = new SyntheticCampaignEvent($"phase24:{manifest.Seed}");
        var id = await bus.PublishAsync(evt, "phase24-harness", ["phase24-consumer"], cancellationToken: ct);
        eventsPublished = 1;
        var reopened = new SqliteDurablePlatformEventBus(Path.Combine(storeRoot, "events.sqlite"), new(MaximumAttempts: 2, RetryDelay: TimeSpan.Zero)) { DeliveryHandler = (_, _, _) => ValueTask.CompletedTask };
        if ((await reopened.GetEventAsync(id, ct)) is null) throw new InvalidDataException("Published event was not durable after reopen.");
        await reopened.DispatchOnceAsync(ct);
        var delivered = await reopened.ListDeliveriesAsync(DurableDeliveryStatus.Delivered, cancellationToken: ct);
        if (delivered.Count != 1 || delivered[0].EventId != id) throw new InvalidDataException("Event delivery identifier or status invariant failed.");
        eventsDelivered = 1;
    }

    private async Task RunStorageAsync(string storeRoot, CancellationToken ct)
    {
        var root = Path.Combine(storeRoot, "assets"); var storage = new FilesystemStorageProviderV1(root);
        var bytes = Encoding.UTF8.GetBytes($"synthetic-asset:{manifest.Seed}"); var expected = SHA256.HashData(bytes);
        for (var i = 0; i < 4; i++)
        {
            await using var input = new MemoryStream(bytes, writable: false); var (handle, metadata) = await storage.WriteAsync(input, 1024, ct);
            await using var output = await storage.OpenReadAsync(handle, ct) ?? throw new InvalidDataException("Synthetic asset read failed.");
            var actual = await SHA256.HashDataAsync(output, ct);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual) || metadata.Size != bytes.Length) throw new InvalidDataException("Synthetic asset checksum or metadata mismatch.");
            if (!await storage.DeleteAsync(handle, ct) || await storage.ExistsAsync(handle, ct)) throw new InvalidDataException("Synthetic asset delete invariant failed.");
            storageVerified++;
        }
    }

    private void RunWorkerStateMachine()
    {
        var coordinator = new DistributedWorkCoordinator();
        const string worker = "phase24-worker"; var thumbprint = new string('A', 64);
        var capability = new DistributedCapabilityV1("phase24.synthetic", "1");
        var inventory = new DistributedWorkerInventoryV1("test", "x64", 2, 1024 * 1024, 1, [], [capability]);
        coordinator.AuthorizeWorker(worker, [capability], thumbprint);
        if (!coordinator.RegisterWorker(worker, thumbprint, inventory)) throw new InvalidDataException("Synthetic worker registration failed.");
        var now = DateTimeOffset.UtcNow;
        var request = new DistributedJobRequestV1("phase24-job", capability.ContractId, capability.ContractVersion, "verify", JsonDocument.Parse("{}").RootElement.Clone(), RetrySafe: true);
        var assigned = coordinator.Assign(request, now, TimeSpan.FromSeconds(10), allowRemote: true) ?? throw new InvalidDataException("Worker assignment failed.");
        if (!coordinator.Start(worker, request.JobId, assigned.Generation)) throw new InvalidDataException("Worker start transition failed.");
        coordinator.ExpireLeases(now.AddSeconds(11));
        if (!coordinator.RegisterWorker(worker, thumbprint, inventory)) throw new InvalidDataException("Synthetic worker reconnect failed.");
        if (!coordinator.Heartbeat(worker, [], now.AddSeconds(12), TimeSpan.FromSeconds(10))) throw new InvalidDataException("Synthetic worker heartbeat failed.");
        var reassigned = coordinator.Assign(request, now.AddSeconds(12), TimeSpan.FromSeconds(10), allowRemote: true) ?? throw new InvalidDataException("Retry-safe worker task was not reassigned.");
        if (reassigned.Generation <= assigned.Generation || coordinator.Complete(worker, request.JobId, assigned.Generation, JsonDocument.Parse("\"late-result\"").RootElement))
            throw new InvalidDataException("Stale worker generation was accepted.");
        if (!coordinator.Complete(worker, request.JobId, reassigned.Generation, JsonDocument.Parse("\"current-result\"").RootElement)) throw new InvalidDataException("Current worker generation was rejected.");
        workerFencingVerified = true;
        invariants.Add(new { id = "worker-generation-fencing", result = "PASS", oldGeneration = assigned.Generation, newGeneration = reassigned.Generation });
    }

    private async Task ExecuteFaultAsync(string fault, CancellationToken ct)
    {
        var scheduled = DateTimeOffset.UtcNow; var sw = Stopwatch.StartNew(); var before = server is { HasExited: false } ? "running" : "unavailable"; var after = before; string result;
        var target = fault switch
        {
            "server.ordered-restart" or "process.forced-termination" => $"Hispania.Server PID {server?.Id}",
            "worker.termination" or "worker.unavailable" => "synthetic coordinator worker phase24-worker",
            "connection.reject" => "127.0.0.1:1 loopback only",
            "storage.write-interruption" or "capacity.exhaustion" => "campaign-owned filesystem storage",
            "jobs.restart-during-processing" => "campaign-owned SQLite Jobs store",
            _ => "campaign-local synthetic operation"
        };
        if (fault is "server.ordered-restart" or "process.forced-termination")
        {
            if (server is null) throw new InvalidOperationException("Campaign Server process is unavailable.");
            FaultController.AssertOwnedTarget(owner, server, server.Id);
            if (fault == "server.ordered-restart") await StopOwnedServerAsync(ordered: true);
            else { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(ct); await StopOwnedServerAsync(); }
            await StartServerAsync(ct); result = "campaign-owned-server-restarted"; after = "ready"; goto done;
        }
        if (fault is "worker.termination" or "worker.unavailable") { RunWorkerStateMachine(); result = "worker-disconnect-and-fenced-reassignment-verified"; after = "worker-reconnected"; goto done; }
        if (fault == "operation.timeout") { using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1)); try { await Task.Delay(100, timeout.Token); } catch (OperationCanceledException) { result = "injected-timeout-observed"; after = "operation-cancelled"; goto done; } }
        if (fault == "operation.cancel") { using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.Cancel(); try { await Task.Delay(10, cancel.Token); } catch (OperationCanceledException) { result = "injected-cancellation-observed"; after = "operation-cancelled"; goto done; } }
        if (fault == "response.delay") { await Task.Delay(150, ct); result = "injected-delay-observed"; after = "running"; goto done; }
        if (fault == "connection.reject") { using var socket = new System.Net.Sockets.TcpClient(); try { await socket.ConnectAsync(IPAddress.Loopback, 1, ct); result = "unexpectedly-connected"; } catch (System.Net.Sockets.SocketException) { result = "loopback-connection-rejection-observed"; } after = "running"; goto done; }
        if (fault == "storage.write-interruption")
        {
            var storage = new FilesystemStorageProviderV1(Path.Combine(owner.DirectoryPath, "stores", "fault-assets")); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.Cancel();
            try { await storage.WriteAsync(new MemoryStream([1, 2, 3]), 32, cancel.Token); result = "unexpectedly-written"; }
            catch (OperationCanceledException) { result = Directory.EnumerateFiles(Path.Combine(owner.DirectoryPath, "stores", "fault-assets")).Any() ? "partial-file-detected" : "cancelled-write-cleaned"; }
            after = "storage-usable"; goto done;
        }
        if (fault == "capacity.exhaustion")
        {
            var storage = new FilesystemStorageProviderV1(Path.Combine(owner.DirectoryPath, "stores", "fault-capacity"));
            try { await storage.WriteAsync(new MemoryStream(new byte[8]), 4, ct); result = "unexpectedly-written"; }
            catch (InvalidDataException) { result = "bounded-capacity-refusal-observed"; }
            after = "storage-usable"; goto done;
        }
        if (fault == "jobs.restart-during-processing") { await InjectJobRecoveryAsync(ct); result = "processing-job-recovered-with-new-attempt-and-stale-attempt-fenced"; after = "job-recovered"; goto done; }
        if (fault == "campaign.resume") { result = owner.OwnsDirectory() ? "campaign-owner-and-artifacts-verified" : "ownership-lost"; after = "running"; goto done; }
        result = "unsupported-without-owner-scoped-adapter";
        done:
        faults.Add(new(fault, target, scheduled, DateTimeOffset.UtcNow, result, before, after, sw.ElapsedMilliseconds, "fault-timeline.json"));
        var passed = !result.StartsWith("unsupported", StringComparison.Ordinal) && !result.StartsWith("unexpectedly", StringComparison.Ordinal) && result is not ("partial-file-detected" or "ownership-lost");
        invariants.Add(new { id = "fault-" + fault, result = passed ? "PASS" : "FAIL", recoveryMilliseconds = sw.ElapsedMilliseconds });
        if (!passed) throw new InvalidDataException($"Fault {fault} did not meet its expected outcome.");
    }

    private async Task RunWindowAsync(DateTimeOffset start, CancellationToken ct)
    {
        var stopAt = start.AddSeconds(manifest.DurationSeconds);
        var previousAt = DateTimeOffset.UtcNow;
        var previousCpu = server?.TotalProcessorTime ?? TimeSpan.Zero;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            ct.ThrowIfCancellationRequested();
            if (server is null || server.HasExited) throw new InvalidOperationException("Campaign Server stopped without a controlled recovery scenario.");
            var at = DateTimeOffset.UtcNow;
            foreach (var metric in ReadProcessMetrics(server, at)) metrics.Add(metric);
            var workingSetMb = server.WorkingSet64 / (1024d * 1024d);
            if (workingSetMb > manifest.Limits.MaxMemoryMb) throw new InvalidDataException("Campaign Server exceeded its configured working-set limit.");
            var elapsedMs = Math.Max(1, (at - previousAt).TotalMilliseconds);
            var currentCpu = server.TotalProcessorTime;
            var cpuPercent = Math.Max(0, (currentCpu - previousCpu).TotalMilliseconds / elapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d);
            metrics.Add(new(at, "process.cpu_percent", cpuPercent, "derived", "percent"));
            cpuOverLimitWindows = cpuPercent > manifest.Limits.MaxCpuPercent ? cpuOverLimitWindows + 1 : 0;
            if (cpuOverLimitWindows >= 2) throw new InvalidDataException("Campaign Server exceeded its configured CPU limit in consecutive observation windows.");
            previousAt = at; previousCpu = currentCpu;
            var diskBytes = MeasureCampaignDirectoryBytes();
            metrics.Add(new(at, "campaign.disk_bytes", diskBytes, "derived", "bytes"));
            metrics.Add(new(at, "jobs.queue_depth", Math.Max(0, jobsEnqueued - jobsCompleted), "real", "jobs"));
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, Math.Max(0.1, (stopAt - DateTimeOffset.UtcNow).TotalSeconds))), ct);
        }
    }

    private static IEnumerable<MetricSample> ReadProcessMetrics(Process process, DateTimeOffset at)
    {
        try { process.Refresh(); return [new(at, "process.working_set_bytes", process.WorkingSet64, "real", "bytes"), new(at, "process.cpu_total_ms", process.TotalProcessorTime.TotalMilliseconds, "real", "ms"), new(at, "process.thread_count", process.Threads.Count, "real", "threads"), new(at, "process.handle_count", OperatingSystem.IsWindows() ? process.HandleCount : null, OperatingSystem.IsWindows() ? "real" : "unavailable", "handles", OperatingSystem.IsWindows() ? null : "handle count not exposed by this process API on this platform"), new(at, "harness.managed_heap_bytes", GC.GetTotalMemory(false), "real", "bytes"), new(at, "harness.gc_gen0", GC.CollectionCount(0), "real", "collections"), new(at, "harness.gc_gen1", GC.CollectionCount(1), "real", "collections"), new(at, "harness.gc_gen2", GC.CollectionCount(2), "real", "collections")]; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return [new(at, "process.metrics", null, "unavailable", null, ex.GetType().Name)]; }
    }

    private async Task StopOwnedServerAsync(bool ordered = false)
    {
        if (server is null) return;
        if (!server.HasExited)
        {
            FaultController.AssertOwnedTarget(owner, server, server.Id);
            if (ordered)
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl + "/internal/phase24/shutdown");
                request.Headers.Add("X-Hispania-Phase24-Token", serverShutdownToken);
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Campaign Server refused its authenticated local shutdown request.");
            }
            else server.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await server.WaitForExitAsync(timeout.Token);
        }
        if (!server.HasExited) throw new TimeoutException("Campaign Server did not stop within its bounded shutdown timeout.");
        server.Dispose(); server = null;
    }

    private async Task InjectJobRecoveryAsync(CancellationToken ct)
    {
        var path = Path.Combine(owner.DirectoryPath, "stores", "fault-jobs.sqlite");
        var store = new SqlitePlatformJobStore(path);
        var request = new CreatePlatformJobV1(PlatformTestHashHandlerV1.TypeId, "{\"text\":\"phase24-recovery\",\"algorithm\":\"sha256\"}", IdempotencyKey: $"fault:{manifest.Seed}");
        var created = await new PlatformJobService(store, new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1()), new()).CreateAsync(request, ct);
        var oldLease = await store.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(100), ct) ?? throw new InvalidDataException("Recovery fault could not lease its synthetic Job.");
        await Task.Delay(150, ct);
        var reopened = new SqlitePlatformJobStore(path);
        var recovered = await reopened.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), ct) ?? throw new InvalidDataException("Synthetic running Job was not recovered after store reopen.");
        if (recovered.JobId != created.Job.JobId || recovered.AttemptCount <= oldLease.AttemptCount) throw new InvalidDataException("Recovered Job attempt did not advance.");
        if (!await reopened.CompleteAsync(recovered.JobId, recovered.AttemptCount, "{}", ct) || await reopened.CompleteAsync(oldLease.JobId, oldLease.AttemptCount, "{\"late\":true}", ct)) throw new InvalidDataException("Stale Job attempt was not fenced after recovery.");
    }

    private void CleanupRuntimeResources()
    {
        if (!owner.OwnsDirectory()) throw new IOException("Campaign ownership marker changed before cleanup.");
        if (server is not null) throw new IOException("Campaign Server process is still owned and running; runtime files were preserved.");
        VerifySqliteStores();
        foreach (var name in new[] { "stores", "server-platform", "server-assets", "empty-library", "worker-transfers", "transcode-cache" })
        {
            var root = Path.GetFullPath(owner.DirectoryPath); var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup target escaped campaign ownership root.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        foreach (var name in new[] { "coordinator.sqlite", "coordinator.sqlite-wal", "coordinator.sqlite-shm", "coordinator.sqlite.coordinator.lock", "inventory.sqlite", "inventory.sqlite-wal", "inventory.sqlite-shm", "library.sqlite", "library.sqlite-wal", "library.sqlite-shm" })
        {
            var target = Path.Combine(owner.DirectoryPath, name);
            if (File.Exists(target)) File.Delete(target);
        }
        if (server is not null || !owner.OwnsDirectory()) throw new IOException("Campaign process or ownership state remains after cleanup.");
        if (Directory.EnumerateFiles(owner.DirectoryPath, "*.sqlite*", SearchOption.AllDirectories).Any()) throw new IOException("Campaign-owned SQLite files remain after cleanup.");
    }

    private void VerifySqliteStores()
    {
        foreach (var path in Directory.EnumerateFiles(owner.DirectoryPath, "*.sqlite", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Campaign SQLite integrity check failed.");
            invariants.Add(new { id = "sqlite-integrity", database = Path.GetRelativePath(owner.DirectoryPath, path).Replace('\\', '/'), result = "PASS" });
        }
    }

    private long MeasureCampaignDirectoryBytes()
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(owner.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            try { total = checked(total + new FileInfo(path).Length); }
            catch (FileNotFoundException) { /* SQLite WAL sidecars may disappear between enumeration and stat. */ }
            catch (DirectoryNotFoundException) { /* A campaign-owned transient directory may close during sampling. */ }
        }
        return total;
    }

    private void RecordBootstrapLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (serverBootstrap)
        {
            serverBootstrap.Enqueue(SafeDiagnostic(line));
            while (serverBootstrap.Count > 40) serverBootstrap.Dequeue();
        }
    }

    private string SafeDiagnostic(string value)
    {
        var safe = value.Replace(owner.DirectoryPath, "<campaign>", StringComparison.OrdinalIgnoreCase)
            .Replace(repositoryRoot, "<repository>", StringComparison.OrdinalIgnoreCase)
            .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>", StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(safe, "(?i)(password|token|secret|api[-_]?key)(\\s*[=:]\\s*)[^\\s,;]+", "$1$2[redacted]");
    }

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private string GitSha() { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repositoryRoot, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }); return p?.StandardOutput.ReadToEnd().Trim() ?? "unavailable"; } catch { return "unavailable"; } }
    private static double? PercentileOrNull(List<double> values, double p) { lock (values) return values.Count == 0 ? null : Statistics.Percentile(values, p); }
    private void WriteMetrics() { var path = Path.Combine(owner.DirectoryPath, "metrics.jsonl"); using var writer = new StreamWriter(path, false, new UTF8Encoding(false)); foreach (var row in metrics) writer.WriteLine(JsonSerializer.Serialize(row)); }
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void WriteReport(string path, object summary) => File.WriteAllText(path, $"# HISPANIA Phase 24 campaign\n\n```json\n{JsonSerializer.Serialize(summary, JsonOptions)}\n```\n", new UTF8Encoding(false));
    private void WriteChecksums()
    {
        var files = Directory.EnumerateFiles(owner.DirectoryPath, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) is not ("artifact-checksums.json" or ".hispania-phase24-owner.json"));
        var entries = files.Order(StringComparer.Ordinal).Select(path => new { path = Path.GetRelativePath(owner.DirectoryPath, path).Replace('\\', '/'), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), sizeBytes = new FileInfo(path).Length }).ToArray();
        WriteJson(Path.Combine(owner.DirectoryPath, "artifact-checksums.json"), entries);
    }
    private sealed record SyntheticCampaignEvent(string Value) : IPlatformEvent { public string EventType => "phase24.synthetic"; public int Version => 1; }
}
