namespace Hispania.Platform.Contracts.Assets;

public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.CreateVersion7());
}

public enum AssetType { Image, Video, Audio, Document, Binary }
public enum AssetStatus { Ready, Quarantined, Deleted }

public sealed record AssetScope(string Domain, string OwnerId);

public sealed record AssetAccessPolicy(string PolicyId, bool PublicRead = false);

public sealed record AssetMetadata(
    AssetId Id,
    AssetType Type,
    string MimeType,
    long Size,
    IReadOnlyDictionary<string, string> Metadata,
    AssetScope Scope,
    AssetAccessPolicy AccessPolicy,
    string ContentHandle = "",
    AssetStatus Status = AssetStatus.Ready,
    DateTimeOffset CreatedAtUtc = default,
    DateTimeOffset UpdatedAtUtc = default);

public sealed record AssetReference(AssetId Id);

public interface IPlatformAssetStore
{
    ValueTask<AssetMetadata> StoreAsync(AssetType type, string mimeType, Stream content, AssetScope scope, AssetAccessPolicy accessPolicy, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    ValueTask<AssetMetadata?> GetMetadataAsync(AssetId id, CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(AssetId id, CancellationToken cancellationToken = default);
}

public interface IPlatformAssetContentProvider
{
    ValueTask<string> StoreAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenReadAsync(string handle, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string handle, CancellationToken cancellationToken = default);
}

public interface IPlatformAssetMetadataRepository
{
    ValueTask SaveAsync(AssetMetadata metadata, CancellationToken cancellationToken = default);
    ValueTask<AssetMetadata?> GetAsync(AssetId id, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(AssetId id, CancellationToken cancellationToken = default);
}
