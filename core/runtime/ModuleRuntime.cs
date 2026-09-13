using Hispania.ModuleSystem.Contracts;
using Microsoft.Extensions.Hosting;

namespace Hispania.Runtime;

public sealed class ModuleRuntime(IModuleRegistry registry) : IModuleRuntime, IHostedService
{
    private readonly object syncRoot = new();
    private readonly Dictionary<string, ModuleHealth> healthByModuleId = registry.List()
        .ToDictionary(
            module => module.Descriptor.Id,
            module => new ModuleHealth(module.Descriptor, ModuleLifecycleState.Registered, false, null),
            StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var module in registry.List())
        {
            SetHealth(module, ModuleLifecycleState.Starting, false, null);

            try
            {
                await module.StartAsync(cancellationToken);
                SetHealth(module, ModuleLifecycleState.Running, true, null);
            }
            catch (Exception exception)
            {
                SetHealth(module, ModuleLifecycleState.Failed, false, exception.Message);
                throw new ModuleStartupException(module.Descriptor.Id, exception);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var module in registry.List().Reverse())
        {
            if (GetHealth(module.Descriptor.Id).State is not ModuleLifecycleState.Running)
            {
                continue;
            }

            SetHealth(module, ModuleLifecycleState.Stopping, true, null);

            try
            {
                await module.StopAsync(cancellationToken);
                SetHealth(module, ModuleLifecycleState.Stopped, false, null);
            }
            catch (Exception exception)
            {
                SetHealth(module, ModuleLifecycleState.Failed, false, exception.Message);
                throw;
            }
        }
    }

    public ModuleHealth GetHealth(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        lock (syncRoot)
        {
            return healthByModuleId.TryGetValue(moduleId, out var health)
                ? health
                : throw new ModuleNotFoundException(moduleId);
        }
    }

    public IReadOnlyCollection<ModuleHealth> ListHealth()
    {
        lock (syncRoot)
        {
            return healthByModuleId.Values.ToArray();
        }
    }

    private void SetHealth(IModule module, ModuleLifecycleState state, bool isHealthy, string? detail)
    {
        lock (syncRoot)
        {
            healthByModuleId[module.Descriptor.Id] = new ModuleHealth(module.Descriptor, state, isHealthy, detail);
        }
    }
}
