using Hispania.ResilienceHarness;
using System.Diagnostics;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase24ResilienceTests
{
    [Fact]
    public void ProfilesAreBoundedAndExtendedIsLongButNotImplicitlyActivated()
    {
        Assert.Equal(60, CampaignProfiles.All["Smoke"].DurationSeconds);
        Assert.InRange(CampaignProfiles.All["Short"].DurationSeconds, 600, 900);
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
}
