using Microsoft.Extensions.Hosting;

namespace Hispania.Core.Persistence;

internal sealed class CorePersistenceLifecycle(ICorePersistence persistence) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => persistence.InitializeAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        await persistence.CheckpointAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
