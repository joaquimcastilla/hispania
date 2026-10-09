using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Storage;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.Storage.Application;
using Hispania.Platform.Storage.Filesystem;
using Hispania.Platform.Maintenance.Application;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase22MaintenanceTests
{
    [Fact]
    public async Task MaintenanceHistoryPrunesOldSuccessButKeepsFailureAndLastSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(root, "maintenance.sqlite");
            var journal = new SqliteMaintenanceRunStoreV1(path);
            for (var index = 0; index < 4; index++)
            {
                var started = now.AddDays(-40 + index);
                var plan = new RetentionPlanV1(Guid.NewGuid(), new("platform.jobs"), new(false),
                    started, started.AddMinutes(10), 0, 0, null, null, null, "test", []);
                await journal.SavePlanAsync(plan, default);
                var run = await journal.BeginAsync(plan, null, started, started.AddMinutes(2), default);
                await journal.FinishAsync(run.RunId, index == 2 ? MaintenanceRunStateV1.Failed : MaintenanceRunStateV1.Completed,
                    index == 2 ? "test-failure" : null, started.AddMinutes(1), default);
            }
            var owner = new SqliteMaintenanceHistoryRetentionOwnerV1(path);
            var policy = new RetentionPolicyV1(true, TimeSpan.FromDays(1), MaximumRetainedCount: 1, BatchSize: 1);
            var retention = await owner.PlanAsync(policy, now, now.AddMinutes(10));
            Assert.Equal(2, retention.CandidateCount);
            Assert.Equal(2, retention.ProtectedCount);
            Assert.Equal(1, (await owner.ExecuteBatchAsync(retention)).Affected);
            Assert.Equal(1, (await owner.ExecuteBatchAsync(retention)).Affected);
            var remaining = await journal.ListAsync(10, default);
            Assert.Equal(2, remaining.Count);
            Assert.Contains(remaining, run => run.State == MaintenanceRunStateV1.Failed);
            Assert.Contains(remaining, run => run.State == MaintenanceRunStateV1.Completed);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SimulatedLongLivedJobHistoryStopsAtConfiguredBatchBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var clock = new MutableClock(now);
            var path = Path.Combine(root, "jobs.sqlite");
            var jobs = new SqlitePlatformJobStore(path, clock);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open(); using var tx = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = tx;
                command.CommandText = "INSERT INTO platform_jobs(job_id,job_type,status,created_at,scheduled_at,completed_at,attempt_count,max_attempts,payload_json,executor) VALUES($id,'test', $status,$at,$at,$at,0,1,'{}','local')";
                var id = command.Parameters.Add("$id", SqliteType.Text);
                command.Parameters.AddWithValue("$status", (int)PlatformJobStatusV1.Succeeded);
                command.Parameters.AddWithValue("$at", now.AddDays(-90).ToString("O"));
                for (var index = 0; index < 1000; index++)
                {
                    id.Value = Guid.NewGuid().ToString("D"); command.ExecuteNonQuery();
                }
                tx.Commit();
            }
            var journal = new SqliteMaintenanceRunStoreV1(Path.Combine(root, "maintenance.sqlite"));
            var coordinator = new MaintenanceCoordinatorV1([jobs], journal, clock);
            var plan = await coordinator.PlanAsync("platform.jobs",
                new RetentionPolicyV1(true, TimeSpan.FromDays(30), BatchSize: 50));
            Assert.Equal(1000, plan.CandidateCount);
            var run = await coordinator.ExecuteAsync(plan.PlanId, maximumBatches: 2);
            Assert.Equal(2, run.Batches);
            Assert.Equal(100, run.Affected);
            Assert.Equal(900, (await coordinator.PlanAsync("platform.jobs",
                new RetentionPolicyV1(true, TimeSpan.FromDays(30), BatchSize: 50))).CandidateCount);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReconciliationAndCapacityMetricsUseOnlyBoundedLabels()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var labels = new ConcurrentBag<(string Key, string Value)>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, subscription) =>
            {
                if (instrument.Meter.Name is "Hispania.Reconciliation" or "Hispania.Capacity")
                    subscription.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags) labels.Add((tag.Key, tag.Value?.ToString() ?? ""));
            });
            listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
            {
                foreach (var tag in tags) labels.Add((tag.Key, tag.Value?.ToString() ?? ""));
            });
            listener.Start();
            var storageRoot = Path.Combine(root, "objects");
            var provider = new FilesystemStorageProviderV1(storageRoot);
            var objectValue = await provider.WriteAsync(new MemoryStream([1]), 10);
            var name = objectValue.Handle.Value["storage://filesystem/".Length..];
            File.SetLastWriteTimeUtc(Path.Combine(storageRoot, name + ".blob"), DateTime.UtcNow.AddHours(-2));
            var metadataPath = Path.Combine(root, "assets.sqlite");
            _ = new SqliteAssetMetadataRepository(metadataPath);
            var owner = new SqliteAssetReconciliationOwnerV1(metadataPath,
                new StorageProviderRegistryV1("filesystem", [provider]), provider, TimeSpan.FromMinutes(1));
            var first = await owner.ScanAsync(1, null);
            _ = await owner.ScanAsync(1, first.ContinuationToken);
            var capacity = new FilesystemCapacitySourceV1(storageRoot, "platform.storage",
                new CapacityThresholdsV1(100, 0.2, 0.05));
            _ = await capacity.ObserveAsync();
            Assert.NotEmpty(labels);
            Assert.All(labels, tag => Assert.True(tag.Key is "owner" or "kind" or "source"));
            Assert.DoesNotContain(labels, tag => tag.Value.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WriteFailureAfterCapacityPreflightRemovesPartialOwnedObject()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new FixedCapacitySource(new(new("test"), DateTimeOffset.UtcNow,
                1000, 900, null, CapacityPressureLevelV1.Normal, true, null));
            var provider = new FilesystemStorageProviderV1(root, source,
                new CapacityThresholdsV1(10, 0.2, 0.05));
            using var content = new ThrowAfterOneReadStream();
            await Assert.ThrowsAsync<IOException>(async () => await provider.WriteAsync(content, 100));
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExpiredJobIdempotencyTombstoneIsPrunedOnlyAfterHorizon()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var clock = new MutableClock(now);
            var path = Path.Combine(root, "jobs.sqlite");
            var jobs = new SqlitePlatformJobStore(path, clock, TimeSpan.FromDays(60));
            var old = Job(now.AddDays(-30), PlatformJobStatusV1.Succeeded, "expiry-key");
            await jobs.CreateAsync(old, default); MarkTerminal(path, old);
            var policy = new RetentionPolicyV1(true, TimeSpan.FromDays(1));
            var first = await jobs.PlanAsync(policy, now, now.AddMinutes(10));
            Assert.Equal(1, (await jobs.ExecuteBatchAsync(first)).Affected);
            Assert.True((await jobs.CreateAsync(Job(now, PlatformJobStatusV1.Pending, "expiry-key"), default)).Duplicate);
            clock.UtcNow = now.AddDays(31);
            var expired = await jobs.PlanAsync(policy, clock.UtcNow, clock.UtcNow.AddMinutes(10));
            Assert.Equal(1, expired.CandidateCount);
            Assert.Equal(1, (await jobs.ExecuteBatchAsync(expired)).Affected);
            Assert.False((await jobs.CreateAsync(Job(clock.UtcNow, PlatformJobStatusV1.Pending, "expiry-key"), default)).Duplicate);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task JobPlanAndPrunePreserveDedupeAndActiveRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var path = Path.Combine(root, "jobs.sqlite");
            var store = new SqlitePlatformJobStore(path, clock, TimeSpan.FromDays(365));
            var completed = Job(now.AddDays(-30), PlatformJobStatusV1.Succeeded, "dedupe-1");
            var active = Job(now.AddDays(-90), PlatformJobStatusV1.Pending, null);
            var future = Job(now.AddDays(1), PlatformJobStatusV1.Succeeded, null);
            await store.CreateAsync(completed, default);
            await store.CreateAsync(active, default);
            await store.CreateAsync(future, default);
            MarkTerminal(path, completed);
            MarkTerminal(path, future);
            var policy = new RetentionPolicyV1(true, TimeSpan.FromDays(10), BatchSize: 2);
            var plan = await store.PlanAsync(policy, now, now.AddMinutes(5));
            Assert.Equal(1, plan.CandidateCount);
            Assert.Equal(2, plan.ProtectedCount);
            Assert.NotNull(await store.GetAsync(completed.JobId, default)); // dry run does not mutate
            Assert.Equal(1, (await store.ExecuteBatchAsync(plan)).Affected);
            Assert.Null(await store.GetAsync(completed.JobId, default));
            Assert.NotNull(await store.GetAsync(active.JobId, default));
            Assert.NotNull(await store.GetAsync(future.JobId, default));

            var duplicate = await store.CreateAsync(Job(now, PlatformJobStatusV1.Pending, "dedupe-1"), default);
            Assert.True(duplicate.Duplicate);
            Assert.True(duplicate.HistoryPruned);
            Assert.Equal(completed.JobId, duplicate.Job.JobId);

            var restarted = new SqlitePlatformJobStore(path, clock, TimeSpan.FromDays(365));
            Assert.Equal(0, (await restarted.ExecuteBatchAsync(plan)).Affected); // at-least-once batch replay
            clock.UtcNow = now.AddDays(400);
            var accepted = await restarted.CreateAsync(Job(clock.UtcNow, PlatformJobStatusV1.Pending, "dedupe-1"), default);
            Assert.False(accepted.Duplicate); // explicit dedupe horizon has elapsed
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task JobRetentionRejectsClockRollbackAndInvalidPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var store = new SqlitePlatformJobStore(Path.Combine(root, "jobs.sqlite"), clock);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.PlanAsync(new RetentionPolicyV1(true, TimeSpan.FromDays(1), BatchSize: 0), now, now.AddMinutes(1)));
            var plan = await store.PlanAsync(new RetentionPolicyV1(true, TimeSpan.FromDays(1)), now, now.AddMinutes(1));
            clock.UtcNow = now.AddTicks(-1);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ExecuteBatchAsync(plan));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task JobRetentionUsesExactUtcBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var path = Path.Combine(root, "jobs.sqlite");
            var store = new SqlitePlatformJobStore(path, new MutableClock(now));
            var cutoff = now.AddDays(-1);
            foreach (var completed in new[] { cutoff.AddTicks(-1), cutoff, cutoff.AddTicks(1), now.AddDays(1) })
            {
                var job = Job(completed, PlatformJobStatusV1.Succeeded, null);
                await store.CreateAsync(job, default); MarkTerminal(path, job);
            }
            var plan = await store.PlanAsync(new RetentionPolicyV1(true, TimeSpan.FromDays(1)), now, now.AddMinutes(1));
            Assert.Equal(2, plan.CandidateCount);
            Assert.Equal(2, plan.ProtectedCount);
            Assert.Equal(2, (await store.ExecuteBatchAsync(plan)).Affected);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MaintenanceRunJournalRecoversExpiredLeaseAndPersistsCheckpoints()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var jobsPath = Path.Combine(root, "jobs.sqlite");
            var jobs = new SqlitePlatformJobStore(jobsPath, clock);
            var old = Job(now.AddDays(-30), PlatformJobStatusV1.Succeeded, "maintenance-test");
            await jobs.CreateAsync(old, default);
            MarkTerminal(jobsPath, old);
            var runsPath = Path.Combine(root, "maintenance.sqlite");
            var journal = new SqliteMaintenanceRunStoreV1(runsPath);
            var coordinator = new MaintenanceCoordinatorV1([jobs], journal, clock);
            var plan = await coordinator.PlanAsync("platform.jobs", new RetentionPolicyV1(true, TimeSpan.FromDays(1)), default);
            var crashed = await journal.BeginAsync(plan, null, now, now.AddMinutes(2), default);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await coordinator.ExecuteAsync(plan.PlanId, 1));
            clock.UtcNow = now.AddMinutes(3);
            var completed = await coordinator.ExecuteAsync(plan.PlanId, 1);
            Assert.Equal(MaintenanceRunStateV1.Completed, completed.State);
            Assert.Equal(1, completed.Batches);
            Assert.Equal(1, completed.Affected);
            var reopened = new SqliteMaintenanceRunStoreV1(runsPath);
            var history = await reopened.ListAsync(10, default);
            Assert.Contains(history, run => run.RunId == crashed.RunId && run.State == MaintenanceRunStateV1.Interrupted);
            Assert.Contains(history, run => run.RunId == completed.RunId && run.State == MaintenanceRunStateV1.Completed);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CrashAfterMutationBeforeCheckpointReplaysWithoutRepeatingDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var jobsPath = Path.Combine(root, "jobs.sqlite");
            var jobs = new SqlitePlatformJobStore(jobsPath, clock);
            var old = Job(now.AddDays(-30), PlatformJobStatusV1.Succeeded, "crash-key");
            await jobs.CreateAsync(old, default); MarkTerminal(jobsPath, old);
            var journal = new SqliteMaintenanceRunStoreV1(Path.Combine(root, "maintenance.sqlite"));
            var coordinator = new MaintenanceCoordinatorV1([jobs], journal, clock);
            var plan = await coordinator.PlanAsync("platform.jobs", new RetentionPolicyV1(true, TimeSpan.FromDays(1)));
            var interrupted = await journal.BeginAsync(plan, null, now, now.AddMinutes(2), default);
            Assert.Equal(1, (await jobs.ExecuteBatchAsync(plan)).Affected); // crash before checkpoint
            clock.UtcNow = now.AddMinutes(3);
            var recovered = await coordinator.ExecuteAsync(plan.PlanId, 1);
            Assert.Equal(0, recovered.Affected);
            Assert.Null(await jobs.GetAsync(old.JobId, default));
            Assert.True((await jobs.CreateAsync(Job(now, PlatformJobStatusV1.Pending, "crash-key"), default)).Duplicate);
            Assert.Contains(await journal.ListAsync(10, default), run => run.RunId == interrupted.RunId && run.State == MaintenanceRunStateV1.Interrupted);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EventRetentionKeepsPendingAndDeadLettersAndRevalidatesReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var path = Path.Combine(root, "events.sqlite");
            Guid delivered, pending, dead;
            using (var bus = new SqliteDurablePlatformEventBus(path))
            {
                delivered = await bus.PublishAsync(new RetentionEvent(), "test", ["test.subscriber"]);
                pending = await bus.PublishAsync(new RetentionEvent(), "test", ["test.subscriber"]);
                dead = await bus.PublishAsync(new RetentionEvent(), "test", ["test.subscriber"]);
            }
            SetDeliveryState(path, delivered, DurableDeliveryStatus.Delivered, now.AddDays(-30));
            SetDeliveryState(path, pending, DurableDeliveryStatus.Pending, now.AddDays(-30));
            SetDeliveryState(path, dead, DurableDeliveryStatus.DeadLettered, now.AddDays(-30));
            var owner = new SqliteEventRetentionOwnerV1(path, clock);
            var plan = await owner.PlanAsync(new RetentionPolicyV1(true, TimeSpan.FromDays(10)), now, now.AddMinutes(5));
            Assert.Equal(1, plan.CandidateCount);
            Assert.Equal(2, plan.ProtectedCount);
            SetDeliveryState(path, delivered, DurableDeliveryStatus.Pending, now.AddDays(-30));
            Assert.Equal(0, (await owner.ExecuteBatchAsync(plan)).Affected); // replay won the race
            SetDeliveryState(path, delivered, DurableDeliveryStatus.Delivered, now.AddDays(-30));
            Assert.Equal(1, (await owner.ExecuteBatchAsync(plan)).Affected);
            using var reopened = new SqliteDurablePlatformEventBus(path);
            Assert.Null(await reopened.GetEventAsync(delivered));
            Assert.NotNull(await reopened.GetEventAsync(pending));
            Assert.NotNull(await reopened.GetEventAsync(dead));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionRetentionKeepsActiveAndFutureTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var clock = new MutableClock(now);
            var path = Path.Combine(root, "identity.sqlite");
            _ = new SqliteIdentityStore(path);
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
                Pooling = false }.ToString()))
            {
                c.Open();
                using var q = c.CreateCommand();
                var userId = Guid.NewGuid().ToString("D");
                q.CommandText = "INSERT INTO identity_users VALUES($user,'testuser','Test User',0,$old,$old)";
                q.Parameters.AddWithValue("$user", userId);
                q.Parameters.AddWithValue("$old", now.AddDays(-100).ToString("O")); q.ExecuteNonQuery();
                foreach (var (expires, revoked) in new[] {
                    (now.AddDays(-30), (DateTimeOffset?)null),
                    (now.AddDays(30), (DateTimeOffset?)now.AddDays(-30)),
                    (now.AddDays(30), (DateTimeOffset?)null),
                    (now.AddDays(100), (DateTimeOffset?)now.AddDays(1)) })
                {
                    q.Parameters.Clear();
                    q.CommandText = "INSERT INTO identity_sessions VALUES($id,$user,$hash,$created,$expires,$revoked)";
                    var id = Guid.NewGuid().ToString("D");
                    q.Parameters.AddWithValue("$id", id); q.Parameters.AddWithValue("$user", userId);
                    q.Parameters.AddWithValue("$hash", id); q.Parameters.AddWithValue("$created", now.AddDays(-100).ToString("O"));
                    q.Parameters.AddWithValue("$expires", expires.ToString("O"));
                    q.Parameters.AddWithValue("$revoked", revoked is { } value ? value.ToString("O") : DBNull.Value);
                    q.ExecuteNonQuery();
                }
            }
            var owner = new SqliteSessionRetentionOwnerV1(path, clock);
            var plan = await owner.PlanAsync(new RetentionPolicyV1(true, TimeSpan.FromDays(10)), now, now.AddMinutes(5));
            Assert.Equal(2, plan.CandidateCount);
            Assert.Equal(2, plan.ProtectedCount);
            Assert.Equal(2, (await owner.ExecuteBatchAsync(plan)).Affected);
            Assert.Equal(0, (await owner.ExecuteBatchAsync(plan)).Affected);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AssetReconciliationFindsMissingMismatchAndGraceProtectedOrphans()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
            var storageRoot = Path.Combine(root, "objects");
            var externalMediaRoot = Path.Combine(root, "user-media");
            Directory.CreateDirectory(externalMediaRoot);
            var userFilm = Path.Combine(externalMediaRoot, "film.mkv");
            await File.WriteAllBytesAsync(userFilm, [7, 8, 9]);
            var provider = new FilesystemStorageProviderV1(storageRoot);
            var resolver = new StorageProviderRegistryV1("filesystem", [provider]);
            var metadataPath = Path.Combine(root, "assets.sqlite");
            var metadata = new SqliteAssetMetadataRepository(metadataPath);
            var healthy = await provider.WriteAsync(new MemoryStream([1, 2]), 10);
            var mismatch = await provider.WriteAsync(new MemoryStream([3, 4]), 10);
            var orphanMature = await provider.WriteAsync(new MemoryStream([5]), 10);
            var orphanFresh = await provider.WriteAsync(new MemoryStream([6]), 10);
            await metadata.SaveAsync(Asset(healthy.Handle.Value, 2, now));
            await metadata.SaveAsync(Asset(mismatch.Handle.Value, 99, now));
            await metadata.SaveAsync(Asset("storage://filesystem/0123456789abcdef0123456789abcdef", 1, now));
            var matureId = orphanMature.Handle.Value["storage://filesystem/".Length..];
            File.SetLastWriteTimeUtc(Path.Combine(storageRoot, matureId + ".blob"), now.AddDays(-2).UtcDateTime);
            var owner = new SqliteAssetReconciliationOwnerV1(metadataPath, resolver, provider,
                TimeSpan.FromDays(1), new MutableClock(now));
            var first = await owner.ScanAsync(500, null);
            Assert.False(first.Complete);
            Assert.Contains(first.Findings, finding => finding.Kind == ReconciliationFindingKindV1.MissingContent);
            Assert.Contains(first.Findings, finding => finding.Kind == ReconciliationFindingKindV1.Mismatch);
            var second = await owner.ScanAsync(500, first.ContinuationToken);
            Assert.True(second.Complete);
            Assert.Contains(second.Findings, finding => finding.SubjectId == orphanMature.Handle.Value && finding.Quarantined);
            Assert.Contains(second.Findings, finding => finding.SubjectId == orphanFresh.Handle.Value && !finding.Quarantined);
            Assert.True(await provider.ExistsAsync(orphanMature.Handle)); // quarantine never removes bytes
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(userFilm));
            var quarantine = await owner.ListQuarantineAsync(100);
            Assert.Single(quarantine);
            Assert.Equal(orphanMature.Handle.Value, quarantine[0].ContentHandle);
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = metadataPath,
                Pooling = false }.ToString());
            c.Open(); using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM asset_reconciliation_quarantine WHERE resolved_utc IS NULL";
            Assert.Equal(1L, q.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static AssetMetadata Asset(string handle, long size, DateTimeOffset created) =>
        new(AssetId.New(), AssetType.Binary, "application/octet-stream", size,
            new Dictionary<string, string>(), new("test", "test"), new("private"), handle,
            AssetStatus.Ready, created, created, "filesystem");


    [Theory]
    [InlineData(1000L, 500L, CapacityPressureLevelV1.Normal)]
    [InlineData(1000L, 90L, CapacityPressureLevelV1.Warning)]
    [InlineData(1000L, 40L, CapacityPressureLevelV1.Critical)]
    public void CapacityThresholdsAndReserve(long total, long free, CapacityPressureLevelV1 expected)
    {
        var thresholds = new CapacityThresholdsV1(10, 0.1, 0.05);
        Assert.Equal(expected, CapacityPolicyV1.Classify(total, free, thresholds));
        var snapshot = new CapacitySnapshotV1(new("test"), DateTimeOffset.UtcNow, total, free, null, expected, true, null);
        Assert.Equal(total - free, snapshot.UsedBytes);
        Assert.Equal(free >= 110, CapacityPolicyV1.CanReserve(snapshot, 100, thresholds));
    }

    [Fact]
    public void UnknownCapacityAndOverflowFailClosed()
    {
        var thresholds = new CapacityThresholdsV1(100, 0.1, 0.05);
        var unknown = new CapacitySnapshotV1(new("test"), DateTimeOffset.UtcNow, null, null, null,
            CapacityPressureLevelV1.Unknown, false, null);
        Assert.False(CapacityPolicyV1.CanReserve(unknown, 1, thresholds));
        Assert.Null(unknown.UsedBytes);
        Assert.False(CapacityPolicyV1.CanReserve(unknown with { FreeBytes = long.MaxValue }, long.MaxValue, thresholds));
        Assert.Throws<InvalidDataException>(() => CapacityPolicyV1.Classify(10, 11, thresholds));
    }

    [Fact]
    public void CapacityPolicyFuzzPreservesThresholdOrderAndReserve()
    {
        var random = new Random(22022);
        var thresholds = new CapacityThresholdsV1(1000, 0.2, 0.05);
        for (var i = 0; i < 1000; i++)
        {
            var total = random.NextInt64(1, 1_000_000_000);
            var free = random.NextInt64(0, total + 1);
            var pressure = CapacityPolicyV1.Classify(total, free, thresholds);
            if (free <= 1000) Assert.Equal(CapacityPressureLevelV1.Critical, pressure);
            var snapshot = new CapacitySnapshotV1(new("test"), DateTimeOffset.UtcNow, total, free, null,
                pressure, true, null);
            Assert.False(CapacityPolicyV1.CanReserve(snapshot, long.MaxValue, thresholds));
            if (free >= 1000) Assert.True(CapacityPolicyV1.CanReserve(snapshot, 0, thresholds));
        }
    }

    private static PlatformJobV1 Job(DateTimeOffset at, PlatformJobStatusV1 status, string? key) =>
        new(Guid.NewGuid(), "platform.test.hash", status, at, at, null,
            status == PlatformJobStatusV1.Pending ? null : at, 0, 1, null, null, null, key,
            "{}", null, null, "local", null);

    private static void MarkTerminal(string path, PlatformJobV1 job)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE platform_jobs SET status=$status,completed_at=$completed WHERE job_id=$id";
        command.Parameters.AddWithValue("$status", (int)job.Status);
        command.Parameters.AddWithValue("$completed", job.CompletedAt!.Value.ToString("O"));
        command.Parameters.AddWithValue("$id", job.JobId.ToString("D"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void SetDeliveryState(string path, Guid eventId, DurableDeliveryStatus status, DateTimeOffset at)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Events SET OccurredAt=$at WHERE EventId=$id; UPDATE Deliveries SET Status=$status,DeliveredAt=$delivered,DeadLetteredAt=$dead WHERE EventId=$id";
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$delivered", status == DurableDeliveryStatus.Delivered ? at.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$dead", status == DurableDeliveryStatus.DeadLettered ? at.ToString("O") : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private sealed record RetentionEvent : IPlatformEvent
    {
        public string EventType => "test.retention";
        public int Version => 1;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixedCapacitySource(CapacitySnapshotV1 snapshot) : ICapacitySourceV1
    {
        public MaintenanceOwnerIdV1 SourceId => snapshot.Source;
        public ValueTask<CapacitySnapshotV1> ObserveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);
    }

    private sealed class ThrowAfterOneReadStream : Stream
    {
        private bool read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new IOException("Injected post-preflight writer failure.");
            read = true; buffer.Span[0] = 1; return ValueTask.FromResult(1);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
