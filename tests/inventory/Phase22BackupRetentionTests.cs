using Hispania.Platform.Contracts.Maintenance;
using Hispania.Server;
using Xunit;

namespace Hispania.Inventory.Tests;

[Trait("Category", "Phase22Retention")]
public sealed class Phase22BackupRetentionTests
{
    [Fact]
    public async Task BackupCapacityPreflightRejectsImpossibleReserveBeforeStaging()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation();
        File.WriteAllText(Path.Combine(fixture.Source, "portable.json"),
            "{\"Persistence:Provider\":\"sqlite\",\"Platform:Capacity:ReserveBytes\":\"9223372036854775807\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default));
        Assert.False(Directory.Exists(fixture.Bundle));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, "bundle.tmp-*"));
    }

    [Fact]
    public async Task MarkedBackupStagingIsPrunedButUnknownAndActiveStagingRemain()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stage = Path.Combine(root, "backup.tmp-" + Guid.NewGuid().ToString("N"));
            var unknown = Path.Combine(root, "unknown.tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage); Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(stage, "partial.txt"), "owned test bytes");
            File.WriteAllText(stage + ".owned", "HISPANIA_BACKUP_STAGING_V1");
            Directory.SetLastWriteTimeUtc(stage, DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddDays(-2));
            var owner = new BackupStagingRetentionOwnerV1(root);
            var now = DateTimeOffset.UtcNow;
            var plan = await owner.PlanAsync(new RetentionPolicyV1(true, TimeSpan.FromDays(1)),
                now, now.AddMinutes(10));
            Assert.Equal(1, plan.CandidateCount);
            using (BackupInstallation.LockArchive(root))
                await Assert.ThrowsAsync<IOException>(async () => await owner.ExecuteBatchAsync(plan));
            Assert.True(Directory.Exists(stage));
            Assert.Equal(1, (await owner.ExecuteBatchAsync(plan)).Affected);
            Assert.False(Directory.Exists(stage));
            Assert.True(Directory.Exists(unknown));
        }
        finally { Directory.Delete(root, true); }
    }


    [Fact]
    public async Task VerifiedArchiveProtectsPinsNewestCopiesAndLastValidAfterFailedBackup()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation();
        var archive = Path.Combine(fixture.Root, "archive");
        Directory.CreateDirectory(archive);
        foreach (var name in new[] { "backup-1", "backup-2", "backup-3" })
        {
            await BackupInstallation.CreateAsync(fixture.Source, Path.Combine(archive, name), default);
            await Task.Delay(20);
        }
        using var owner = new BackupArchiveRetentionOwnerV1(archive);
        var policy = new RetentionPolicyV1(true, TimeSpan.Zero, MaximumRetainedCount: 2, BatchSize: 1);
        var now = DateTimeOffset.UtcNow;
        var plan = await owner.PlanAsync(policy, now, now.AddMinutes(10));
        Assert.Equal(1, plan.CandidateCount);
        Assert.Equal(2, plan.ProtectedCount);
        File.WriteAllText(Path.Combine(archive, "backup-1.pin"), "operator-selected");
        var pinned = await owner.PlanAsync(policy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(0, pinned.CandidateCount);
        Assert.Equal(0, (await owner.ExecuteBatchAsync(plan)).Affected);
        File.Delete(Path.Combine(archive, "backup-1.pin"));
        var renewed = await owner.PlanAsync(policy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(1, (await owner.ExecuteBatchAsync(renewed)).Affected);
        Assert.False(Directory.Exists(Path.Combine(archive, "backup-1")));
        Assert.True(Directory.Exists(Path.Combine(archive, "backup-2")));
        Assert.True(Directory.Exists(Path.Combine(archive, "backup-3")));

        Directory.CreateDirectory(Path.Combine(archive, "backup-4"));
        File.WriteAllText(Path.Combine(archive, "backup-4", "manifest.json"), "{invalid");
        var oneCopyPolicy = policy with { MaximumRetainedCount = 1 };
        var older = await owner.PlanAsync(oneCopyPolicy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(1, older.CandidateCount);
        Assert.Equal(1, (await owner.ExecuteBatchAsync(older)).Affected);
        var last = await owner.PlanAsync(oneCopyPolicy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(0, last.CandidateCount);
        Assert.True(Directory.Exists(Path.Combine(archive, "backup-3")));
        Assert.True(Directory.Exists(Path.Combine(archive, "backup-4")));
    }
}
