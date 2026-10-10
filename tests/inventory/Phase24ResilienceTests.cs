using Hispania.ResilienceHarness;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase24ResilienceTests
{
    [Fact]
    public void ProfilesAreBoundedAndExtendedIsLongButNotImplicitlyActivated()
    {
        Assert.Equal(60, CampaignProfiles.All["Smoke"].DurationSeconds);
        Assert.Equal(900, CampaignProfiles.All["Short"].DurationSeconds);
        Assert.Equal(7200, CampaignProfiles.All["Soak"].DurationSeconds);
        Assert.InRange(CampaignProfiles.All["Extended"].DurationSeconds, 28800, 86400);
    }

    [Fact]
    public void ManifestRequiresDefinedLimitsAndStableWorkloadsAndFaultIds()
    {
        var valid = ResilienceValidation.Create("unit-campaign", CampaignProfiles.All["Smoke"], 42);
        Assert.Equal(valid, ResilienceValidation.Validate(valid));
        Assert.Throws<InvalidDataException>(() => ResilienceValidation.Validate(valid with { DurationSeconds = 86401 }));
        Assert.Throws<InvalidDataException>(() => ResilienceValidation.Validate(valid with { Limits = valid.Limits with { MaxConcurrency = 0 } }));
        Assert.Throws<InvalidDataException>(() => ResilienceValidation.Validate(valid with { Workloads = ["health", "health"] }));
        Assert.Throws<InvalidDataException>(() => ResilienceValidation.Validate(valid with { Faults = ["external.endpoint.kill"] }));
        Assert.Throws<InvalidDataException>(() => ResilienceValidation.Validate(valid with { Workloads = null! }));
    }

    [Fact]
    public void SeedProducesStableManifestAndPercentilesUseLinearInterpolation()
    {
        var first = ResilienceValidation.Create("same", CampaignProfiles.All["Smoke"], 24001);
        var second = ResilienceValidation.Create("same", CampaignProfiles.All["Smoke"], 24001);
        Assert.Equal(first.Campaign, second.Campaign); Assert.Equal(first.Seed, second.Seed); Assert.Equal(first.Workloads, second.Workloads); Assert.Equal(first.Limits, second.Limits);
        Assert.Equal(2.5, Statistics.Percentile([1, 2, 3, 4], 50));
        Assert.Equal(3.85, Statistics.Percentile([1, 2, 3, 4], 95), 6);
    }

    [Fact]
    public async Task SustainedWorkloadLoopRepeatsUntilItsWindowEnds()
    {
        var count = 0;
        await SustainedWorkloadLoop.RunUntilAsync(DateTimeOffset.UtcNow.AddMilliseconds(360), 100,
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(count >= 3);
    }

    [Fact]
    public async Task SustainedWorkloadLoopStopsOnCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var task = SustainedWorkloadLoop.RunUntilAsync(DateTimeOffset.UtcNow.AddMinutes(1), 100,
            _ => { Interlocked.Increment(ref count); return Task.CompletedTask; }, cancellation.Token);
        await Task.Delay(150);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var stoppedAt = Volatile.Read(ref count);
        await Task.Delay(150);
        Assert.Equal(stoppedAt, Volatile.Read(ref count));
    }

    [Fact]
    public void BoundedLatencyStatisticsRetainPercentilesAndClearBetweenIntervals()
    {
        var latency = new BoundedLatencyStatistics();
        for (var i = 1; i <= 100; i++) latency.Add(i);
        Assert.Equal(50, latency.Percentile(50));
        Assert.Equal(95, latency.Percentile(95));
        Assert.Equal(99, latency.Percentile(99));
        latency.Reset();
        Assert.Null(latency.Percentile(50));
    }

    [Fact]
    public void WorkloadCountersTrackAttemptsErrorsAndTimeoutsAndCoverageIsTruthful()
    {
        var metrics = new WorkloadOperationMetrics();
        metrics.Record("health");
        metrics.Record("health", error: true);
        metrics.Record("jobs", timeout: true);
        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.Operations["health"]);
        Assert.Equal(1, snapshot.Errors["health"]);
        Assert.Equal(1, snapshot.Operations["jobs"]);
        Assert.Equal(1, snapshot.Timeouts["jobs"]);
        Assert.Contains(CampaignCoverage.Limitations, text => text.Contains("does not exercise a remote WorkerAgent", StringComparison.Ordinal));
        Assert.Contains(CampaignCoverage.Limitations, text => text.Contains("not managed heap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShortCampaignRepeatsAllWorkloadsAndWritesIntervalMetricsAndCleanupEvidence()
    {
        var repo = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase24-campaign-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = ResilienceValidation.Create("campaign-test", CampaignProfiles.All["Smoke"], 2424,
                new ResilienceLimits(1024, 80, 2), workloadIntervalMilliseconds: 100);
            manifest = manifest with { DurationSeconds = 3 };
            var owner = new CampaignOwnership(root, manifest.Campaign);
            var result = await new CampaignRunner(manifest, owner, repo).RunAsync(CancellationToken.None);
            Assert.Equal(0, result);
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(owner.DirectoryPath, "campaign-summary.json")));
            var document = summary.RootElement;
            Assert.Equal("PASS", document.GetProperty("status").GetString());
            Assert.True(document.GetProperty("sustainedDurationSeconds").GetDouble() >= 3);
            Assert.True(document.GetProperty("intervalMetrics").GetArrayLength() >= 2);
            var counts = document.GetProperty("metrics").GetProperty("workloadOperations");
            foreach (var workload in new[] { "health", "jobs", "events", "storage", "worker" })
                Assert.True(counts.GetProperty(workload).GetInt64() > 0, $"{workload} did not repeat during the campaign.");
            Assert.Equal("passed", document.GetProperty("cleanup").GetString());
            Assert.Contains(CampaignCoverage.Limitations, text => text.Contains("remote WorkerAgent", StringComparison.Ordinal));
            owner.Dispose();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ShortCampaignTrendIsInconclusiveAndLongTrendRequiresSustainedGrowth()
    {
        Assert.Equal("INCONCLUSIVE", Statistics.Trend(Enumerable.Range(0, 20).Select(i => (double)i).ToArray(), shortCampaign: true));
        Assert.Equal("FAIL", Statistics.Trend(Enumerable.Range(1, 20).Select(i => (double)i * 100).ToArray(), shortCampaign: false));
        Assert.Equal("PASS", Statistics.Trend(Enumerable.Repeat(100d, 20).ToArray(), shortCampaign: false));
    }

    [Fact]
    public void OwnershipMarkerMustBePresentBeforeCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase24-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owner = new CampaignOwnership(root, "unit-test");
            Assert.True(owner.OwnsDirectory());
            Assert.Throws<IOException>(() => new CampaignOwnership(root, "unit-test"));
            File.Delete(Path.Combine(owner.DirectoryPath, ".hispania-phase24-owner.json"));
            Assert.False(owner.OwnsDirectory());
            Assert.Throws<IOException>(owner.Dispose);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void FaultTargetMustMatchCampaignProcessManifestAndPid()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase24-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owner = new CampaignOwnership(root, "process-test");
            using var current = Process.GetCurrentProcess();
            owner.RecordOwnedProcess(current, "unit-test-process");
            FaultController.AssertOwnedTarget(owner, current, current.Id);
            Assert.Throws<InvalidOperationException>(() => FaultController.AssertOwnedTarget(owner, current, current.Id + 1));
            Assert.Throws<IOException>(owner.Dispose);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hispania.sln")) && Directory.Exists(Path.Combine(directory.FullName, "apps", "server"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the HISPANIA repository root for the Phase24 campaign test.");
    }
}
