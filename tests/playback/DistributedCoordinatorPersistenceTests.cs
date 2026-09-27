using System.Text.Json;
using Hispania.DistributedWork.Persistence.Sqlite;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Xunit;
using Microsoft.Data.Sqlite;

namespace Hispania.Playback.Tests;

public sealed class DistributedCoordinatorPersistenceTests
{
    [Fact]
    public void SqliteStateAllowsOnlyOneCoordinator()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-distributed-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "coordinator.sqlite");
        var store = new SqliteDistributedWorkStateStore(database);
        try
        {
            Assert.Throws<IOException>(() => new SqliteDistributedWorkStateStore(database));
        }
        finally
        {
            store.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RestartRecoversPendingWorkAndFencesPreviousGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-distributed-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "coordinator.sqlite");
        var node = "worker-a";
        const string thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
        var capability = new DistributedCapabilityV1("test.echo", "1.0");
        var inventory = new DistributedWorkerInventoryV1("test", "x64", 2, 1024, 1, [], [capability]);
        var request = new DistributedJobRequestV1("restart-job", capability.ContractId, capability.ContractVersion, "echo", JsonDocument.Parse("{\"value\":1}").RootElement.Clone(), RetrySafe: true);
        DistributedJobLeaseV1 oldLease;
        using (var store = new SqliteDistributedWorkStateStore(database))
        {
            var coordinator = new DistributedWorkCoordinator(store);
            coordinator.AuthorizeWorker(node, [capability], thumbprint);
            Assert.True(coordinator.RegisterWorker(node, thumbprint, inventory));
            coordinator.Queue(request, allowRemote: true);
            oldLease = Assert.IsType<DistributedJobLeaseV1>(coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))?.Lease);
            Assert.True(coordinator.Start(node, oldLease.JobId, oldLease.Generation));
        }

        using (var store = new SqliteDistributedWorkStateStore(database))
        {
            var coordinator = new DistributedWorkCoordinator(store);
            Assert.Equal(DistributedJobStateV1.Pending, Assert.Single(coordinator.ListJobs()).State);
            Assert.True(coordinator.RegisterWorker(node, thumbprint, inventory));
            var newLease = coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))!.Lease;
            Assert.True(newLease.Generation > oldLease.Generation);
            using var result = JsonDocument.Parse("{\"ok\":true}");
            Assert.False(coordinator.Complete(node, oldLease.JobId, oldLease.Generation, result.RootElement));
            Assert.True(coordinator.Complete(node, newLease.JobId, newLease.Generation, result.RootElement));
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void RestartRecoversRetrySafeWorkFromTransferEncodingAndPublicationPhases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-distributed-phase-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "coordinator.sqlite");
        const string node = "worker-a";
        const string thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
        var capability = new DistributedCapabilityV1("test.phase", "1.0");
        var inventory = new DistributedWorkerInventoryV1("test", "x64", 4, 1024, 3, [], [capability]);
        var stages = new[] { "transfer", "encoding", "publication" };
        var orderedJobs = stages.Select(static stage => "restart-" + stage).OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        var oldGenerations = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var store = new SqliteDistributedWorkStateStore(database))
        {
            var coordinator = new DistributedWorkCoordinator(store);
            coordinator.AuthorizeWorker(node, [capability], thumbprint);
            Assert.True(coordinator.RegisterWorker(node, thumbprint, inventory));
            foreach (var jobId in orderedJobs)
                coordinator.Queue(new DistributedJobRequestV1(jobId, capability.ContractId, capability.ContractVersion,
                    jobId["restart-".Length..], JsonDocument.Parse("{}").RootElement.Clone(), RetrySafe: true), allowRemote: true);
            foreach (var jobId in orderedJobs)
            {
                var lease = coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))!.Lease;
                Assert.Equal(jobId, lease.JobId);
                oldGenerations.Add(lease.JobId, lease.Generation);
                Assert.True(coordinator.Start(node, lease.JobId, lease.Generation));
            }
        }

        using (var store = new SqliteDistributedWorkStateStore(database))
        {
            var coordinator = new DistributedWorkCoordinator(store);
            Assert.All(coordinator.ListJobs(), job => Assert.Equal(DistributedJobStateV1.Pending, job.State));
            Assert.True(coordinator.RegisterWorker(node, thumbprint, inventory));
            foreach (var jobId in orderedJobs)
            {
                var lease = coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))!.Lease;
                Assert.Equal(jobId, lease.JobId);
                Assert.True(lease.Generation > oldGenerations[lease.JobId]);
                Assert.False(coordinator.Complete(node, lease.JobId, oldGenerations[lease.JobId], JsonDocument.Parse("true").RootElement));
                Assert.True(coordinator.Complete(node, lease.JobId, lease.Generation, JsonDocument.Parse("true").RootElement));
            }
            Assert.All(coordinator.ListJobs(), job => Assert.Equal(DistributedJobStateV1.Completed, job.State));
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void RestartDoesNotRepeatUnsafeUncertainWorkAndRejectsExpiredLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-distributed-uncertain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "coordinator.sqlite");
        const string node = "worker-a";
        const string thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
        var capability = new DistributedCapabilityV1("test.effect", "1.0");
        var inventory = new DistributedWorkerInventoryV1("test", "x64", 2, 1024, 1, [], [capability]);
        var request = new DistributedJobRequestV1("unsafe-restart-job", capability.ContractId, capability.ContractVersion,
            "non-repeatable", JsonDocument.Parse("{}").RootElement.Clone(), HasExternalSideEffects: false, RetrySafe: false);
        DistributedJobLeaseV1 oldLease;
        using (var store = new SqliteDistributedWorkStateStore(database))
        {
            var coordinator = new DistributedWorkCoordinator(store);
            coordinator.AuthorizeWorker(node, [capability], thumbprint);
            Assert.True(coordinator.RegisterWorker(node, thumbprint, inventory));
            coordinator.Queue(request, allowRemote: true);
            oldLease = coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))!.Lease;
            Assert.True(coordinator.Start(node, oldLease.JobId, oldLease.Generation));
            Assert.Equal(1, coordinator.ExpireLeases(DateTimeOffset.UtcNow.AddMinutes(2)));
            Assert.False(coordinator.Complete(node, oldLease.JobId, oldLease.Generation, JsonDocument.Parse("true").RootElement));
            Assert.Equal(DistributedJobStateV1.Uncertain, Assert.Single(coordinator.ListJobs()).State);
        }

        using (var store = new SqliteDistributedWorkStateStore(database))
        {
            var coordinator = new DistributedWorkCoordinator(store);
            Assert.Equal(DistributedJobStateV1.Uncertain, Assert.Single(coordinator.ListJobs()).State);
            Assert.True(coordinator.RegisterWorker(node, thumbprint, inventory));
            Assert.Null(coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
            Assert.False(coordinator.Complete(node, oldLease.JobId, oldLease.Generation, JsonDocument.Parse("true").RootElement));
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}
