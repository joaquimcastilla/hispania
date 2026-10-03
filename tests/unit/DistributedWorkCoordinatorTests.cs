using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class DistributedWorkCoordinatorTests
{
    private static readonly DistributedCapabilityV1 Ffmpeg = new("hispania.playback.compatible-transcoder", "1.0");
    private const string CertificateThumbprint = "1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly DistributedWorkerInventoryV1 Inventory = new("Linux", "x64", 8, 8_000_000_000, 2, ["nvenc"], [Ffmpeg]);

    [Fact]
    public void UnknownWorkerCannotRegisterAndAuthorizedWorkerRequiresMatchingCapabilities()
    {
        var coordinator = new DistributedWorkCoordinator();
        Assert.False(coordinator.RegisterWorker("unknown", CertificateThumbprint, Inventory));

        coordinator.AuthorizeWorker("worker-a", [Ffmpeg], CertificateThumbprint);
        Assert.False(coordinator.RegisterWorker("worker-a", CertificateThumbprint, Inventory with { Capabilities = [new DistributedCapabilityV1("other", "1")] }));
        Assert.True(coordinator.RegisterWorker("worker-a", CertificateThumbprint, Inventory));
    }

    [Fact]
    public void LeaseExpiryReassignsOnlyRetrySafeWorkAndFencesOldGeneration()
    {
        var coordinator = RegisteredCoordinator();
        var now = DateTimeOffset.UtcNow;
        var request = Request("retryable", retrySafe: true);
        var first = coordinator.Assign(request, now, TimeSpan.FromSeconds(10), allowRemote: true)!;
        Assert.True(coordinator.Start(first.NodeId, first.JobId, first.Generation));

        Assert.Equal(1, coordinator.ExpireLeases(now + TimeSpan.FromSeconds(11)));
        Assert.True(coordinator.Heartbeat("worker-a", [], now + TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10)));
        var second = coordinator.Assign(request, now + TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10), allowRemote: true)!;
        Assert.True(second.Generation > first.Generation);
        Assert.False(coordinator.Complete(first.NodeId, first.JobId, first.Generation, JsonSerializer.SerializeToElement("stale")));
        Assert.True(coordinator.Complete(second.NodeId, second.JobId, second.Generation, JsonSerializer.SerializeToElement("valid")));
        Assert.Equal(DistributedJobStateV1.Completed, Assert.Single(coordinator.ListJobs()).State);
    }

    [Fact]
    public void LeaseEligibilityRequiresAuthorizationCapabilityConnectionFreshHeartbeatAndCapacity()
    {
        var coordinator = new DistributedWorkCoordinator();
        var limited = Inventory with { MaximumConcurrentJobs = 1 };
        coordinator.AuthorizeWorker("worker-a", [Ffmpeg], CertificateThumbprint);
        Assert.True(coordinator.RegisterWorker("worker-a", CertificateThumbprint, limited));
        var now = DateTimeOffset.UtcNow;

        Assert.Null(coordinator.Assign(Request("missing-capability", retrySafe: true) with { ContractId = "unregistered.capability" }, now, TimeSpan.FromSeconds(10), true));
        var first = coordinator.Assign(Request("capacity-one", retrySafe: true), now, TimeSpan.FromSeconds(10), true);
        Assert.NotNull(first);
        Assert.Null(coordinator.Assign(Request("capacity-two", retrySafe: true), now, TimeSpan.FromSeconds(10), true));

        coordinator.DisconnectWorker("worker-a", CertificateThumbprint);
        Assert.Null(coordinator.Assign(Request("disconnected", retrySafe: true), now, TimeSpan.FromSeconds(10), true));
        Assert.True(coordinator.RegisterWorker("worker-a", CertificateThumbprint, limited));
        coordinator.ExpireLeases(now.AddSeconds(31));
        Assert.Null(coordinator.Assign(Request("stale-heartbeat", retrySafe: true), now.AddSeconds(31), TimeSpan.FromSeconds(10), true));

        coordinator.RevokeWorker("worker-a");
        Assert.Null(coordinator.Assign(Request("revoked", retrySafe: true), now.AddSeconds(32), TimeSpan.FromSeconds(10), true));
    }

    [Fact]
    public async Task TwoAuthorizedWorkersCannotLeaseSameGenerationAndFailoverFencesLateResult()
    {
        var coordinator = RegisteredCoordinator();
        coordinator.AuthorizeWorker("worker-b", [Ffmpeg], new string('2', 64));
        Assert.True(coordinator.RegisterWorker("worker-b", new string('2', 64), Inventory));
        var now = DateTimeOffset.UtcNow;
        var request = Request("two-workers", retrySafe: true);
        coordinator.Queue(request, allowRemote: true);
        var firstA = Task.Run(() => coordinator.LeaseNext("worker-a", now, TimeSpan.FromSeconds(10)));
        var firstB = Task.Run(() => coordinator.LeaseNext("worker-b", now, TimeSpan.FromSeconds(10)));
        var firstLeases = await Task.WhenAll(firstA, firstB);
        var first = Assert.Single(firstLeases, static lease => lease is not null)!;
        Assert.True(coordinator.Start(first.Lease.NodeId, first.Lease.JobId, first.Lease.Generation));

        Assert.Equal(1, coordinator.ExpireLeases(now.AddSeconds(11)));
        Assert.True(coordinator.RegisterWorker("worker-a", CertificateThumbprint, Inventory));
        Assert.True(coordinator.RegisterWorker("worker-b", new string('2', 64), Inventory));
        var secondA = Task.Run(() => coordinator.LeaseNext("worker-a", now.AddSeconds(12), TimeSpan.FromSeconds(10)));
        var secondB = Task.Run(() => coordinator.LeaseNext("worker-b", now.AddSeconds(12), TimeSpan.FromSeconds(10)));
        var secondLeases = await Task.WhenAll(secondA, secondB);
        var second = Assert.Single(secondLeases, static lease => lease is not null)!;
        Assert.True(second.Lease.Generation > first.Lease.Generation);
        Assert.True(coordinator.Start(second.Lease.NodeId, second.Lease.JobId, second.Lease.Generation));
        Assert.True(coordinator.Complete(second.Lease.NodeId, second.Lease.JobId, second.Lease.Generation, JsonSerializer.SerializeToElement("current")));
        Assert.False(coordinator.Complete(first.Lease.NodeId, first.Lease.JobId, first.Lease.Generation, JsonSerializer.SerializeToElement("late")));
        Assert.Equal("current", coordinator.ListJobs().Single().Result?.GetString());
    }

    [Fact]
    public void LeaseExpiryMarksNonIdempotentWorkUncertainAndNeverSelectsSideEffects()
    {
        var coordinator = RegisteredCoordinator();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(coordinator.Assign(Request("side-effect", sideEffects: true, retrySafe: true), now, TimeSpan.FromSeconds(10), allowRemote: true));
        var request = Request("uncertain", retrySafe: false);
        var lease = coordinator.Assign(request, now, TimeSpan.FromSeconds(10), allowRemote: true)!;
        Assert.Equal(1, coordinator.ExpireLeases(now + TimeSpan.FromSeconds(11)));

        Assert.Null(coordinator.Assign(request, now + TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10), allowRemote: true));
        Assert.Equal(DistributedJobStateV1.Uncertain, Assert.Single(coordinator.ListJobs()).State);
        Assert.False(coordinator.Complete(lease.NodeId, lease.JobId, lease.Generation, JsonSerializer.SerializeToElement("late")));
    }

    [Fact]
    public void RemoteDisabledAndRevokedWorkersReceiveNoAssignments()
    {
        var coordinator = RegisteredCoordinator();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(coordinator.Assign(Request("local", retrySafe: true), now, TimeSpan.FromSeconds(10), allowRemote: false));
        coordinator.RevokeWorker("worker-a");
        Assert.Null(coordinator.Assign(Request("revoked", retrySafe: true), now, TimeSpan.FromSeconds(10), allowRemote: true));
        Assert.False(coordinator.ListWorkers().Single().Authorized);
    }

    [Fact]
    public void CancellingQueuedOrLeasedWorkFencesLateCompletion()
    {
        var coordinator = RegisteredCoordinator();
        var now = DateTimeOffset.UtcNow;
        var queued = Request("queued-cancel", retrySafe: true);
        coordinator.Queue(queued, allowRemote: true);
        Assert.True(coordinator.CancelJob(queued.JobId));
        Assert.Equal(DistributedJobStateV1.Cancelled, Assert.Single(coordinator.ListJobs()).State);

        var running = coordinator.Assign(Request("running-cancel", retrySafe: true), now, TimeSpan.FromMinutes(1), allowRemote: true)!;
        Assert.True(coordinator.Start(running.NodeId, running.JobId, running.Generation));
        Assert.True(coordinator.Cancel(running.NodeId, running.JobId, running.Generation));
        Assert.False(coordinator.Complete(running.NodeId, running.JobId, running.Generation, JsonSerializer.SerializeToElement("late")));
        Assert.Contains(coordinator.ListJobs(), job => job.Request.JobId == running.JobId && job.State == DistributedJobStateV1.Cancelled);
    }

    private static DistributedWorkCoordinator RegisteredCoordinator()
    {
        var coordinator = new DistributedWorkCoordinator();
        coordinator.AuthorizeWorker("worker-a", [Ffmpeg], CertificateThumbprint);
        Assert.True(coordinator.RegisterWorker("worker-a", CertificateThumbprint, Inventory));
        return coordinator;
    }

    private static DistributedJobRequestV1 Request(string id, bool sideEffects = false, bool retrySafe = false) => new(
        id, Ffmpeg.ContractId, Ffmpeg.ContractVersion, "Transcode", JsonSerializer.SerializeToElement(new { input = "opaque" }), sideEffects, retrySafe, $"corr-{id}");
}
