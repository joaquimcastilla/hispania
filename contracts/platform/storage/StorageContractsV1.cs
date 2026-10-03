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

public sealed class StorageProviderNotFoundException(string providerId)
    : InvalidOperationException($"Storage provider '{providerId}' is not registered.")
{
    public string ProviderId { get; } = providerId;
}
