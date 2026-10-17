namespace Hispania.Core.Persistence;

/// <summary>The narrow provider-neutral persistence boundary currently needed by the Core.</summary>
internal interface ICorePersistence
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<CoreInstanceState> GetInstanceStateAsync(CancellationToken cancellationToken);
    Task CheckpointAsync(CancellationToken cancellationToken);
}

internal sealed record CoreInstanceState(Guid InstallationId, DateTimeOffset CreatedUtc);

internal sealed class CorePersistenceException(string safeMessage) : Exception(safeMessage);
