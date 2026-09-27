using System.Security.Cryptography;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Server;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class DistributedMediaTransferTests
{
    [Fact]
    public async Task InterruptedUploadLeavesNoPartialAndStaleLeaseCannotPublish()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-media-transfer-interruption-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Enumerable.Range(0, 256 * 1024).Select(static index => (byte)index).ToArray();
            using var registry = new DistributedMediaTransferRegistry(root, bytes.Length * 4L);
            var grant = registry.RegisterSource("interrupted-job", 4, bytes.Length * 4L,
                _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3, 4], writable: false)), TimeSpan.FromMinutes(1));
            using var cancellation = new CancellationTokenSource();
            var reachedUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var blocked = new BlockingUploadStream(bytes, reachedUpload);
            var upload = registry.ReceiveResultAsync("interrupted-job", 7, grant.TransferId, grant.AccessToken,
                bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), blocked, cancellationToken: cancellation.Token).AsTask();
            await reachedUpload.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var activePartial = Assert.Single(Directory.EnumerateFiles(root, "*.partial"));
            registry.CleanupExpiredArtifacts();
            Assert.True(File.Exists(activePartial), "Periodic expiry cleanup must not remove an active upload.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.media"));

            var stale = await registry.ReceiveResultAsync("interrupted-job", 7, grant.TransferId, grant.AccessToken,
                bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new MemoryStream(bytes),
                isLeaseCurrent: static () => false);
            Assert.Null(stale);
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.media"));

            var current = await registry.ReceiveResultAsync("interrupted-job", 8, grant.TransferId, grant.AccessToken,
                bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new MemoryStream(bytes),
                isLeaseCurrent: static () => true);
            Assert.NotNull(current);
            Assert.Single(Directory.EnumerateFiles(root, "*.media"));
            Assert.True(registry.RemovePublishedResult(current!));
            Assert.Empty(Directory.EnumerateFiles(root, "*.media"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StartupCleanupRemovesAbandonedAndExpiredFilesButKeepsLivePublishedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-media-transfer-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var abandoned = Path.Combine(root, "worker-crashed.partial");
            var expired = Path.Combine(root, "expired.media");
            var live = Path.Combine(root, "live.media");
            await File.WriteAllBytesAsync(abandoned, [1, 2, 3]);
            await File.WriteAllBytesAsync(expired, [4, 5, 6]);
            await File.WriteAllBytesAsync(live, [7, 8, 9]);
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-25));

            using var registry = new DistributedMediaTransferRegistry(root, 1024);
            Assert.False(File.Exists(abandoned));
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(live));
            await using var liveResult = await registry.OpenPublishedResultAsync("live");
            Assert.NotNull(liveResult);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublicationRejectedAfterLeaseExpiresIsRemovedAndNeverConsumable()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-media-transfer-publish-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var registry = new DistributedMediaTransferRegistry(root);
            var coordinator = new DistributedWorkCoordinator();
            const string node = "publication-worker";
            const string thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
            var capability = new DistributedCapabilityV1("test.publish", "1.0");
            coordinator.AuthorizeWorker(node, [capability], thumbprint);
            Assert.True(coordinator.RegisterWorker(node, thumbprint, new DistributedWorkerInventoryV1("test", "x64", 1, 1024, 1, [], [capability])));
            coordinator.Queue(new DistributedJobRequestV1("publish-gap", capability.ContractId, capability.ContractVersion,
                "transcode", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone(), RetrySafe: true), allowRemote: true);
            var lease = coordinator.LeaseNext(node, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))!.Lease;
            Assert.True(coordinator.Start(node, lease.JobId, lease.Generation));
            var grant = registry.RegisterSource(lease.JobId, 4, 1024,
                _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3, 4], writable: false)), TimeSpan.FromMinutes(1));
            var result = new byte[] { 7, 8, 9 };
            var handle = await registry.ReceiveResultAsync(lease.JobId, lease.Generation, grant.TransferId, grant.AccessToken,
                result.Length, Convert.ToHexString(SHA256.HashData(result)), new MemoryStream(result),
                isLeaseCurrent: () => coordinator.ListJobs().Any(job => job.Request.JobId == lease.JobId &&
                    job.Generation == lease.Generation && job.State == DistributedJobStateV1.Running && job.LeaseExpiresAtUtc > DateTimeOffset.UtcNow));
            Assert.NotNull(handle);

            Assert.Equal(1, coordinator.ExpireLeases(DateTimeOffset.UtcNow.AddMinutes(2)));
            Assert.False(coordinator.Complete(node, lease.JobId, lease.Generation, System.Text.Json.JsonDocument.Parse("{}").RootElement));
            Assert.True(registry.RemovePublishedResult(handle!));
            Assert.Null(await registry.OpenPublishedResultAsync(handle!));
            Assert.Empty(Directory.EnumerateFiles(root, "*.media"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AggregatePublishedStorageCeilingIsEnforcedAndReleasedOnRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-media-transfer-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Enumerable.Range(0, 4096).Select(static index => (byte)(index % 256)).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(output));
            using var registry = new DistributedMediaTransferRegistry(root, output.Length);
            DistributedMediaTransferGrantV1 Grant(string jobId) => registry.RegisterSource(jobId, 1, output.Length,
                _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)), TimeSpan.FromMinutes(1));
            var firstGrant = Grant("cap-one");
            var secondGrant = Grant("cap-two");
            var firstHandle = await registry.ReceiveResultAsync("cap-one", 1, firstGrant.TransferId, firstGrant.AccessToken,
                output.Length, hash, new MemoryStream(output));
            Assert.NotNull(firstHandle);
            Assert.Null(await registry.ReceiveResultAsync("cap-two", 1, secondGrant.TransferId, secondGrant.AccessToken,
                output.Length, hash, new MemoryStream(output)));
            Assert.True(registry.RemovePublishedResult(firstHandle!));
            Assert.NotNull(await registry.ReceiveResultAsync("cap-two", 1, secondGrant.TransferId, secondGrant.AccessToken,
                output.Length, hash, new MemoryStream(output)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GrantAllowsOnlyBoundedJobScopedRangesAndPublishesIntegrityCheckedOpaqueResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-media-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray();
            using var registry = new DistributedMediaTransferRegistry(root);
            var grant = registry.RegisterSource("job-one", source.Length, 1024,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(source, writable: false)), TimeSpan.FromMinutes(1));
            await using (var range = await registry.OpenSourceBlockAsync("job-one", grant.TransferId, grant.AccessToken, 100, 50))
            {
                Assert.NotNull(range);
                using var received = new MemoryStream(); await range!.CopyToAsync(received);
                Assert.Equal(source[100..150], received.ToArray());
            }
            Assert.Null(await registry.OpenSourceBlockAsync("job-two", grant.TransferId, grant.AccessToken, 0, 10));
            Assert.Null(await registry.OpenSourceBlockAsync("job-one", grant.TransferId, "wrong-token", 0, 10));
            Assert.Null(await registry.OpenSourceBlockAsync("job-one", grant.TransferId, grant.AccessToken, 0, DistributedMediaTransferRegistry.MaximumBlockBytes + 1));

            var output = System.Text.Encoding.UTF8.GetBytes("validated transcode output");
            var hash = Convert.ToHexString(SHA256.HashData(output));
            Assert.Null(await registry.ReceiveResultAsync("job-one", 3, grant.TransferId, grant.AccessToken, output.Length, new string('0', 64), new MemoryStream(output)));
            var handle = await registry.ReceiveResultAsync("job-one", 3, grant.TransferId, grant.AccessToken, output.Length, hash, new MemoryStream(output));
            Assert.NotNull(handle);
            Assert.StartsWith("distributed-", handle);
            Assert.DoesNotContain(root, handle, StringComparison.OrdinalIgnoreCase);
            await using var published = await registry.OpenPublishedResultAsync(handle!);
            Assert.NotNull(published);
            using var bytes = new MemoryStream(); await published!.CopyToAsync(bytes);
            Assert.Equal(output, bytes.ToArray());
            registry.Revoke("job-one");
            Assert.Null(await registry.OpenSourceBlockAsync("job-one", grant.TransferId, grant.AccessToken, 0, 10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class BlockingUploadStream(byte[] bytes, TaskCompletionSource reachedUpload) : MemoryStream(bytes, writable: false)
    {
        private int blocked;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                reachedUpload.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
