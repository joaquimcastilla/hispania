using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Server;

/// <summary>Ephemeral, generation-fenced media grants. Disk paths remain private to the coordinator.</summary>
public sealed class DistributedMediaTransferRegistry : IDistributedMediaTransferBrokerV1, IDisposable
{
    public const int MaximumBlockBytes = 4 * 1024 * 1024;
    private const long AbsoluteMaximumSourceBytes = 100L * 1024 * 1024 * 1024;
    private readonly string root;
    private readonly long maximumStorageBytes;
    private long storedBytes;
    private readonly ConcurrentDictionary<string, Grant> grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Published> published = new(StringComparer.Ordinal);

    public DistributedMediaTransferRegistry(string temporaryRoot, long maximumStorageBytes = 40L * 1024 * 1024 * 1024)
    {
        if (maximumStorageBytes <= 0 || maximumStorageBytes > 500L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumStorageBytes));
        this.maximumStorageBytes = maximumStorageBytes;
        root = Path.GetFullPath(temporaryRoot);
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root, "*.partial")) TryDelete(file);
        foreach (var file in Directory.EnumerateFiles(root, "*.media"))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24)) TryDelete(file);
            else
            {
                var handle = Path.GetFileNameWithoutExtension(file);
                published[handle] = new Published(string.Empty, 0, file, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).AddHours(24), info.Length);
                storedBytes += info.Length;
            }
        }
    }

    public DistributedMediaTransferGrantV1 RegisterSource(string jobId, long sourceLength, long maximumResultBytes,
        Func<CancellationToken, ValueTask<Stream>> openSource, TimeSpan lifetime)
    {
        CleanupExpired();
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128) throw new ArgumentException("Invalid job ID.", nameof(jobId));
        if (sourceLength <= 0 || sourceLength > AbsoluteMaximumSourceBytes) throw new ArgumentOutOfRangeException(nameof(sourceLength));
        if (maximumResultBytes <= 0 || maximumResultBytes > AbsoluteMaximumSourceBytes) throw new ArgumentOutOfRangeException(nameof(maximumResultBytes));
        ArgumentNullException.ThrowIfNull(openSource);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(6)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        Revoke(jobId);
        var id = Guid.NewGuid().ToString("N");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow + lifetime;
        grants[jobId] = new Grant(id, token, SHA256.HashData(Convert.FromBase64String(token)), sourceLength, maximumResultBytes, expires, openSource);
        return new DistributedMediaTransferGrantV1(id, token, sourceLength, maximumResultBytes, expires);
    }

    public DistributedMediaTransferGrantV1? GetGrant(string jobId, string transferId)
    {
        CleanupExpired();
        return grants.TryGetValue(jobId, out var grant) && grant.ExpiresAtUtc > DateTimeOffset.UtcNow && grant.TransferId == transferId
            ? new DistributedMediaTransferGrantV1(grant.TransferId, grant.AccessToken, grant.SourceLength, grant.MaximumResultBytes, grant.ExpiresAtUtc)
            : null;
    }

    public async ValueTask<Stream?> OpenSourceBlockAsync(string jobId, string transferId, string accessToken, long offset, int length, CancellationToken cancellationToken = default)
    {
        if (!TryGetGrant(jobId, transferId, accessToken, out var grant) || offset < 0 || length is < 1 or > MaximumBlockBytes || offset >= grant.SourceLength || length > grant.SourceLength - offset) return null;
        if (!grant.TryConsumeSourceBytes(length)) return null;
        var stream = await grant.OpenSource(cancellationToken).ConfigureAwait(false);
        if (!stream.CanRead || !stream.CanSeek) { await stream.DisposeAsync().ConfigureAwait(false); return null; }
        stream.Seek(offset, SeekOrigin.Begin);
        return new BoundedReadStream(stream, length);
    }

    public async ValueTask<string?> ReceiveResultAsync(string jobId, long generation, string transferId, string accessToken,
        long declaredLength, string sha256, Stream content, Func<bool>? isLeaseCurrent = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetGrant(jobId, transferId, accessToken, out var grant) || generation < 1 || declaredLength is <= 0 || declaredLength > grant.MaximumResultBytes ||
            sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;
        if (!grant.TryConsumeResultBytes(declaredLength) || !TryReserveStorage(declaredLength)) return null;
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".partial");
        var actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var kept = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (total < declaredLength)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, declaredLength - total)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) return null;
                    total += read; actualHash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0) return null;
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var actual = Convert.ToHexString(actualHash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(sha256))) return null;
            // Recheck after the potentially long upload and immediately before making the artifact visible.
            if (isLeaseCurrent is not null && !isLeaseCurrent()) return null;
            var handle = "distributed-" + Guid.NewGuid().ToString("N");
            var finalPath = Path.Combine(root, handle + ".media");
            File.Move(temporary, finalPath);
            published[handle] = new Published(jobId, generation, finalPath, DateTimeOffset.UtcNow.AddHours(24), declaredLength);
            kept = true;
            return handle;
        }
        finally { actualHash.Dispose(); TryDelete(temporary); if (!kept) Interlocked.Add(ref storedBytes, -declaredLength); }
    }

    public ValueTask<Stream?> OpenPublishedResultAsync(string publishedHandle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpired();
        if (!published.TryGetValue(publishedHandle, out var artifact) || artifact.ExpiresAtUtc <= DateTimeOffset.UtcNow) return ValueTask.FromResult<Stream?>(null);
        try { return ValueTask.FromResult<Stream?>(new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)); }
        catch (IOException) { if (published.TryRemove(publishedHandle, out var removed)) Interlocked.Add(ref storedBytes, -removed.Length); return ValueTask.FromResult<Stream?>(null); }
    }

    public bool RemovePublishedResult(string publishedHandle)
    {
        if (!published.TryRemove(publishedHandle, out var artifact)) return false;
        TryDelete(artifact.Path);
        Interlocked.Add(ref storedBytes, -artifact.Length);
        return true;
    }

    public void Revoke(string jobId)
    {
        grants.TryRemove(jobId, out _);
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in grants.Where(item => item.Value.ExpiresAtUtc <= now)) grants.TryRemove(item.Key, out _);
        foreach (var item in published.Where(item => item.Value.ExpiresAtUtc <= now))
            if (published.TryRemove(item.Key, out var artifact)) { TryDelete(artifact.Path); Interlocked.Add(ref storedBytes, -artifact.Length); }
    }

    private bool TryReserveStorage(long bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref storedBytes);
            if (bytes > maximumStorageBytes - current) return false;
            if (Interlocked.CompareExchange(ref storedBytes, current + bytes, current) == current) return true;
        }
    }

    public void Dispose() { }
    public void CleanupExpiredArtifacts() => CleanupExpired();

    private bool TryGetGrant(string jobId, string transferId, string token, out Grant grant)
    {
        grant = null!;
        if (!grants.TryGetValue(jobId, out var value) || value.ExpiresAtUtc <= DateTimeOffset.UtcNow || value.TransferId != transferId) return false;
        try
        {
            var candidate = SHA256.HashData(Convert.FromBase64String(token));
            if (!CryptographicOperations.FixedTimeEquals(candidate, value.TokenHash)) return false;
        }
        catch (FormatException) { return false; }
        grant = value;
        return true;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private sealed class Grant(string transferId, string accessToken, byte[] tokenHash, long sourceLength, long maximumResultBytes, DateTimeOffset expiresAtUtc, Func<CancellationToken, ValueTask<Stream>> openSource)
    {
        private long servedSourceBytes;
        private long acceptedResultBytes;
        public string TransferId { get; } = transferId;
        public string AccessToken { get; } = accessToken;
        public byte[] TokenHash { get; } = tokenHash;
        public long SourceLength { get; } = sourceLength;
        public long MaximumResultBytes { get; } = maximumResultBytes;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public Func<CancellationToken, ValueTask<Stream>> OpenSource { get; } = openSource;
        public bool TryConsumeSourceBytes(int count) => Interlocked.Add(ref servedSourceBytes, count) <= SourceLength * 3;
        public bool TryConsumeResultBytes(long count) => Interlocked.Add(ref acceptedResultBytes, count) <= MaximumResultBytes * 2;
    }
    private sealed record Published(string JobId, long Generation, string Path, DateTimeOffset ExpiresAtUtc, long Length);

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => remaining; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (remaining == 0) return 0; var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false); remaining -= read; return read; }
        public override int Read(byte[] buffer, int offset, int count) { if (remaining == 0) return 0; var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class DistributedMediaTransferCleanupService(DistributedMediaTransferRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) registry.CleanupExpiredArtifacts();
    }
}
