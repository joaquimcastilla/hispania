namespace Hispania.Platform.Contracts.Storage;

public sealed record StorageObjectHandle(string Value);

public sealed record StorageObjectMetadata(long Size, string? Sha256 = null, DateTimeOffset? CreatedAtUtc = null);

public sealed record StorageProviderHealth(bool IsHealthy, string? Description = null);

public interface IStorageProviderV1
{
    string ProviderId { get; }
    ValueTask<(StorageObjectHandle Handle, StorageObjectMetadata Metadata)> WriteAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenReadAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default);
    ValueTask<bool> ExistsAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default);
    ValueTask<StorageObjectMetadata?> GetMetadataAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default);
    ValueTask<StorageProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public interface IStorageProviderResolverV1
{
    string DefaultProviderId { get; }
    IStorageProviderV1 Resolve(string providerId);
    IStorageProviderV1 Resolve(StorageObjectHandle handle);
}

/// <summary>Optional owner-local inventory for reconciliation; handles remain opaque to consumers.</summary>
public sealed record StorageInventoryObjectV1(StorageObjectHandle Handle, long Size, DateTimeOffset CreatedAtUtc);
public sealed record StorageInventoryPageV1(IReadOnlyList<StorageInventoryObjectV1> Objects,
    string? ContinuationToken, bool Complete);
public interface IStorageInventoryV1
{
    string ProviderId { get; }
    ValueTask<StorageInventoryPageV1> ScanAsync(int maximumItems, string? continuationToken,
        CancellationToken cancellationToken = default);
}

public sealed class StorageProviderNotFoundException(string providerId)
    : InvalidOperationException($"Storage provider '{providerId}' is not registered.")
{
    public string ProviderId { get; } = providerId;
}
