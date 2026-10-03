using Hispania.Platform.Contracts.Storage;

namespace Hispania.Platform.Storage.Application;

public sealed class StorageProviderRegistryV1 : IStorageProviderResolverV1
{
    private readonly Dictionary<string, IStorageProviderV1> _providers;

    public StorageProviderRegistryV1(string defaultProviderId, IEnumerable<IStorageProviderV1> providers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderId);
        var registered = new Dictionary<string, IStorageProviderV1>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderId);
            if (!registered.TryAdd(provider.ProviderId, provider)) throw new ArgumentException($"Duplicate storage provider '{provider.ProviderId}'.", nameof(providers));
        }
        if (!registered.ContainsKey(defaultProviderId)) throw new StorageProviderNotFoundException(defaultProviderId);
        DefaultProviderId = defaultProviderId;
        _providers = registered;
    }

    public string DefaultProviderId { get; }

    public IStorageProviderV1 Resolve(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : throw new StorageProviderNotFoundException(providerId);

    public IStorageProviderV1 Resolve(StorageObjectHandle handle)
    {
        if (string.IsNullOrWhiteSpace(handle.Value) || !Uri.TryCreate(handle.Value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("storage", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || uri.Port != -1 ||
            uri.AbsolutePath.Length < 2 || uri.AbsolutePath.Contains('%') || uri.AbsolutePath.Contains('\\') ||
            uri.AbsolutePath.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Storage handle is malformed.", nameof(handle));
        return Resolve(uri.Host);
    }
}
