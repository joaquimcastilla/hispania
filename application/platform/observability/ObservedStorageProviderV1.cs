using System.Diagnostics;
using Hispania.Platform.Contracts.Observability;
using Hispania.Platform.Contracts.Storage;

namespace Hispania.Platform.Observability.Application;

public sealed class ObservedStorageProviderV1(IStorageProviderV1 inner, IPlatformMetricsV1 metrics) : IStorageProviderV1
{
    public string ProviderId => inner.ProviderId;
    public ValueTask<(StorageObjectHandle Handle, StorageObjectMetadata Metadata)> WriteAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default) =>
        ExecuteAsync("writes", () => inner.WriteAsync(content, maximumBytes, cancellationToken));
    public ValueTask<Stream?> OpenReadAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => ExecuteAsync("reads", () => inner.OpenReadAsync(handle, cancellationToken));
    public ValueTask<bool> DeleteAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => ExecuteAsync("deletes", () => inner.DeleteAsync(handle, cancellationToken));
    public ValueTask<bool> ExistsAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => inner.ExistsAsync(handle, cancellationToken);
    public ValueTask<StorageObjectMetadata?> GetMetadataAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => inner.GetMetadataAsync(handle, cancellationToken);
    public ValueTask<StorageProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
    private async ValueTask<T> ExecuteAsync<T>(string operation, Func<ValueTask<T>> execute)
    {
        using var activity = PlatformTracingV1.StartScope("storage." + operation);
        var started = Stopwatch.GetTimestamp();
        metrics.BestEffort(sink => sink.Increment("hispania.storage." + operation));
        try { return await execute().ConfigureAwait(false); }
        catch (Exception)
        {
            metrics.BestEffort(sink => { sink.Increment("hispania.storage.failures"); if (operation is "reads" or "writes") sink.Increment("hispania.storage." + (operation == "reads" ? "read" : "write") + ".failure"); });
            throw;
        }
        finally { metrics.BestEffort(sink => sink.Observe("hispania.storage.duration", Stopwatch.GetElapsedTime(started).TotalMilliseconds)); }
    }
}
