using System.Collections.Concurrent;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;

namespace Hispania.Runtime;

/// <summary>Volatile default adapters intended for local development and tests.</summary>
public sealed class InMemoryPlatformAuditWriter : IPlatformAuditWriter
{
    private readonly ConcurrentQueue<AuditRecord> records = new();
    public IReadOnlyCollection<AuditRecord> Records => records.ToArray();
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        records.Enqueue(record);
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryPlatformAssetStore : IPlatformAssetStore
{
    private sealed record Stored(AssetMetadata Metadata, byte[] Content);
    private readonly ConcurrentDictionary<AssetId, Stored> assets = new();

    public async ValueTask<AssetMetadata> StoreAsync(AssetType type, string mimeType, Stream content, AssetScope scope, AssetAccessPolicy accessPolicy, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(accessPolicy);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var id = AssetId.New();
        var bytes = buffer.ToArray();
        var asset = new AssetMetadata(id, type, mimeType, bytes.LongLength, new Dictionary<string, string>(metadata ?? new Dictionary<string, string>()), scope, accessPolicy);
        assets[id] = new(asset, bytes);
        return asset;
    }

    public ValueTask<AssetMetadata?> GetMetadataAsync(AssetId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(assets.TryGetValue(id, out var stored) ? stored.Metadata : null);
    }

    public ValueTask<Stream?> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream?>(assets.TryGetValue(id, out var stored) ? new MemoryStream(stored.Content, writable: false) : null);
    }

    public ValueTask<bool> DeleteAsync(AssetId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(assets.TryRemove(id, out _));
    }
}
