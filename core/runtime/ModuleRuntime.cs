using System.Diagnostics.Metrics;
using Hispania.ModuleSystem.Contracts;
using Microsoft.Extensions.Hosting;

namespace Hispania.Runtime;

/// <summary>In-process module supervisor. Module instances are statically registered and are not unloaded.</summary>
public sealed class ModuleRuntime : IModuleRuntime, IHostedService
{
    private static readonly Meter Meter = new("Hispania.Runtime.Modules", "1.0");
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>("hispania.modules.transitions");
    private readonly IModuleRegistry registry;
    private readonly object syncRoot = new();
    private readonly Dictionary<string, ModuleHealth> healthByModuleId;

    public ModuleRuntime(IModuleRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        healthByModuleId = registry.List().ToDictionary(
            module => module.Descriptor.Id,
            module => new ModuleHealth(module.Descriptor, ModuleLifecycleState.Registered, false, null, DateTimeOffset.UtcNow),
            StringComparer.Ordinal);
    }

    public event Action<ModuleHealthTransition>? StateChanged;

    public async Task RegisterAndStartAsync(IModule moduleInstance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleInstance);
        lock (syncRoot)
        {
            registry.Register(moduleInstance);
            healthByModuleId.Add(moduleInstance.Descriptor.Id, new ModuleHealth(moduleInstance.Descriptor, ModuleLifecycleState.Registered, false, null, DateTimeOffset.UtcNow));
        }
        await StartModuleAsync(moduleInstance.Descriptor.Id, cancellationToken).ConfigureAwait(false);
    }

    public void UnregisterStoppedModule(string moduleId)
    {
        lock (syncRoot)
        {
            if (!healthByModuleId.TryGetValue(moduleId, out var health)) throw new ModuleNotFoundException(moduleId);
            if (health.State is not (ModuleLifecycleState.Stopped or ModuleLifecycleState.Registered or ModuleLifecycleState.Failed))
                throw new InvalidOperationException($"Module '{moduleId}' must be stopped before it can be unloaded.");
            registry.Unregister(moduleId);
            healthByModuleId.Remove(moduleId);
        }
    }

    /// <summary>Registers a module that has already passed its isolated candidate start/health check during a version switch.</summary>
    public void RegisterStartedModule(IModule moduleInstance)
    {
        ArgumentNullException.ThrowIfNull(moduleInstance);
        ValidateDependencies(moduleInstance);
        lock (syncRoot)
        {
            registry.Register(moduleInstance);
            healthByModuleId.Add(moduleInstance.Descriptor.Id, new ModuleHealth(moduleInstance.Descriptor, ModuleLifecycleState.Running, true, null, DateTimeOffset.UtcNow));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var module in GetStartupOrder()) await StartModuleAsync(module.Descriptor.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var module in GetStartupOrder().AsEnumerable().Reverse())
        {
            var state = GetHealth(module.Descriptor.Id).State;
            if (state is ModuleLifecycleState.Running or ModuleLifecycleState.Degraded or ModuleLifecycleState.Draining)
                await StopModuleAsync(module.Descriptor.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var module = registry.GetById(moduleId);
        var state = GetHealth(moduleId).State;
        if (state == ModuleLifecycleState.Running) return;
        if (state is not (ModuleLifecycleState.Registered or ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed))
            throw new InvalidModuleTransitionException(moduleId, state, ModuleLifecycleState.Starting);
        ValidateDependencies(module);
        SetHealth(module, ModuleLifecycleState.Starting, false, null);
        try
        {
            await module.StartAsync(cancellationToken).ConfigureAwait(false);
            var isHealthy = await IsModuleHealthyAsync(module, cancellationToken).ConfigureAwait(false);
            if (!isHealthy) throw new InvalidOperationException($"Module '{moduleId}' failed its startup health check.");
            SetHealth(module, ModuleLifecycleState.Running, true, null);
            await RecoverDependentsAsync(module, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetHealth(module, ModuleLifecycleState.Failed, false, exception.Message);
            throw new ModuleStartupException(moduleId, exception);
        }
    }

    public Task DrainModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = registry.GetById(moduleId);
        var health = GetHealth(moduleId);
        if (health.State == ModuleLifecycleState.Draining) return Task.CompletedTask;
        if (health.State is not (ModuleLifecycleState.Running or ModuleLifecycleState.Degraded))
            throw new InvalidModuleTransitionException(moduleId, health.State, ModuleLifecycleState.Draining);
        SetHealth(module, ModuleLifecycleState.Draining, false, null);
        return Task.CompletedTask;
    }

    public Task MarkDegradedAsync(string moduleId, string detail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = registry.GetById(moduleId);
        var current = GetHealth(moduleId);
        if (current.State == ModuleLifecycleState.Degraded) return Task.CompletedTask;
        if (current.State != ModuleLifecycleState.Running) throw new InvalidModuleTransitionException(moduleId, current.State, ModuleLifecycleState.Degraded);
        SetHealth(module, ModuleLifecycleState.Degraded, false, detail);
        return Task.CompletedTask;
    }

    public async Task StopModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var module = registry.GetById(moduleId);
        var state = GetHealth(moduleId).State;
        if (state is ModuleLifecycleState.Stopped or ModuleLifecycleState.Registered) return;
        if (state is ModuleLifecycleState.Running or ModuleLifecycleState.Degraded)
        {
            SetHealth(module, ModuleLifecycleState.Draining, false, null);
            state = ModuleLifecycleState.Draining;
        }
        if (state != ModuleLifecycleState.Draining) throw new InvalidModuleTransitionException(moduleId, state, ModuleLifecycleState.Stopping);

        SetHealth(module, ModuleLifecycleState.Stopping, false, null);
        try
        {
            await module.StopAsync(cancellationToken).ConfigureAwait(false);
            SetHealth(module, ModuleLifecycleState.Stopped, false, null);
            DegradeDependents(module);
        }
        catch (Exception exception)
        {
            SetHealth(module, ModuleLifecycleState.Failed, false, exception.Message);
            throw;
        }
    }

    public async Task ResumeModuleAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = registry.GetById(moduleId);
        var health = GetHealth(moduleId);
        if (health.State == ModuleLifecycleState.Running) return;
        if (health.State is ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed)
        {
            await StartModuleAsync(moduleId, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (health.State != ModuleLifecycleState.Draining) throw new InvalidModuleTransitionException(moduleId, health.State, ModuleLifecycleState.Running);
        if (!await IsModuleHealthyAsync(module, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Module '{moduleId}' did not pass its resume health check.");
        SetHealth(module, ModuleLifecycleState.Running, true, null);
    }

    public async ValueTask<bool> CheckHealthAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var module = registry.GetById(moduleId);
        var healthy = await IsModuleHealthyAsync(module, cancellationToken).ConfigureAwait(false) && DependenciesAreRunning(module);
        var current = GetHealth(moduleId);
        if (!healthy && current.State == ModuleLifecycleState.Running)
            SetHealth(module, ModuleLifecycleState.Degraded, false, "Module health check failed.");
        else if (healthy && current.State == ModuleLifecycleState.Degraded)
            SetHealth(module, ModuleLifecycleState.Running, true, null);
        return healthy;
    }

    public ModuleHealth GetHealth(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        lock (syncRoot) return healthByModuleId.TryGetValue(moduleId, out var health)
            ? health
            : throw new ModuleNotFoundException(moduleId);
    }

    public IReadOnlyCollection<ModuleHealth> ListHealth()
    {
        lock (syncRoot) return healthByModuleId.Values.OrderBy(health => health.Descriptor.Id, StringComparer.Ordinal).ToArray();
    }

    private List<IModule> GetStartupOrder()
    {
        var modules = registry.List().ToDictionary(module => module.Descriptor.Id, StringComparer.Ordinal);
        var ordered = new List<IModule>(modules.Count);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(IModule module)
        {
            var id = module.Descriptor.Id;
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ModuleDependencyException($"Module dependency cycle includes '{id}'.");
            foreach (var dependency in module.Descriptor.Dependencies)
            {
                if (!modules.TryGetValue(dependency.ModuleId, out var provider))
                    throw new ModuleDependencyException($"Module '{id}' requires missing module '{dependency.ModuleId}'.");
                if (provider.Descriptor.Version < dependency.MinimumVersion)
                    throw new ModuleDependencyException($"Module '{id}' requires '{dependency.ModuleId}' version {dependency.MinimumVersion} or newer.");
                Visit(provider);
            }
            foreach (var requirement in module.Descriptor.Requires)
            {
                var providers = ResolveCapabilityProviders(module, requirement, modules.Values);
                if (providers.Length == 0)
                    throw new ModuleDependencyException($"Module '{id}' requires unavailable capability '{requirement.ContractId}@{requirement.Version}'.");
                if (providers.Length > 1)
                    throw new ModuleDependencyException($"Module '{id}' requires ambiguous capability '{requirement.ContractId}@{requirement.Version}'. Declare exactly one explicit module dependency on a provider.");
                Visit(providers[0]);
            }
            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(module);
        }
        foreach (var module in registry.List().OrderBy(module => module.Descriptor.Id, StringComparer.Ordinal)) Visit(module);
        return ordered;
    }

    private void ValidateDependencies(IModule module)
    {
        foreach (var dependency in module.Descriptor.Dependencies)
        {
            var provider = registry.GetById(dependency.ModuleId);
            if (provider.Descriptor.Version < dependency.MinimumVersion || GetHealth(dependency.ModuleId).State != ModuleLifecycleState.Running)
                throw new ModuleDependencyException($"Dependency '{dependency.ModuleId}' for '{module.Descriptor.Id}' is not running at the required version.");
        }
        foreach (var capability in module.Descriptor.Requires)
        {
            var providers = ResolveCapabilityProviders(module, capability, registry.List());
            if (providers.Length != 1 || GetHealth(providers[0].Descriptor.Id).State != ModuleLifecycleState.Running)
                throw new ModuleDependencyException($"Required capability '{capability.ContractId}@{capability.Version}' for '{module.Descriptor.Id}' is unavailable.");
        }
    }

    private bool DependenciesAreRunning(IModule module)
    {
        foreach (var dependency in module.Descriptor.Dependencies)
        {
            var dependencyHealth = GetHealth(dependency.ModuleId);
            if (dependencyHealth.Descriptor.Version < dependency.MinimumVersion || dependencyHealth.State != ModuleLifecycleState.Running) return false;
        }
        foreach (var capability in module.Descriptor.Requires)
        {
            var providers = ResolveCapabilityProviders(module, capability, registry.List());
            if (providers.Length != 1 || GetHealth(providers[0].Descriptor.Id).State != ModuleLifecycleState.Running) return false;
        }
        return true;
    }

    private static IModule[] ResolveCapabilityProviders(IModule consumer, ModuleCapability required, IEnumerable<IModule> candidates)
    {
        var matching = candidates.Where(candidate => candidate.Descriptor.Provides.Any(provided =>
            provided.ContractId == required.ContractId && provided.Version == required.Version)).ToArray();
        var explicitlySelected = matching.Where(candidate => consumer.Descriptor.Dependencies.Any(dependency =>
            dependency.ModuleId == candidate.Descriptor.Id)).ToArray();
        return explicitlySelected.Length == 0 ? matching : explicitlySelected;
    }

    private void DegradeDependents(IModule stoppedProvider)
    {
        var provided = stoppedProvider.Descriptor.Provides.ToArray();
        foreach (var dependent in registry.List())
        {
            if (dependent.Descriptor.Id == stoppedProvider.Descriptor.Id || GetHealth(dependent.Descriptor.Id).State != ModuleLifecycleState.Running) continue;
            var reliesOnProvider = dependent.Descriptor.Dependencies.Any(dependency => dependency.ModuleId == stoppedProvider.Descriptor.Id) ||
                dependent.Descriptor.Requires.Any(required => provided.Any(capability =>
                    capability.ContractId == required.ContractId && capability.Version == required.Version) &&
                    ResolveCapabilityProviders(dependent, required, registry.List()).Any(provider => provider.Descriptor.Id == stoppedProvider.Descriptor.Id));
            if (reliesOnProvider) SetHealth(dependent, ModuleLifecycleState.Degraded, false, $"Required module '{stoppedProvider.Descriptor.Id}' stopped.");
        }
    }

    private async Task RecoverDependentsAsync(IModule startedProvider, CancellationToken cancellationToken)
    {
        foreach (var dependent in registry.List())
        {
            if (dependent.Descriptor.Id == startedProvider.Descriptor.Id || GetHealth(dependent.Descriptor.Id).State != ModuleLifecycleState.Degraded || !DependenciesAreRunning(dependent)) continue;
            if (await IsModuleHealthyAsync(dependent, cancellationToken).ConfigureAwait(false)) SetHealth(dependent, ModuleLifecycleState.Running, true, null);
        }
    }

    private static ValueTask<bool> IsModuleHealthyAsync(IModule module, CancellationToken cancellationToken) =>
        module is IModuleHealthCheck check ? check.IsHealthyAsync(cancellationToken) : ValueTask.FromResult(true);

    private void SetHealth(IModule module, ModuleLifecycleState state, bool isHealthy, string? detail)
    {
        ModuleHealthTransition transition;
        lock (syncRoot)
        {
            var previous = healthByModuleId[module.Descriptor.Id];
            if (previous.State == state) return;
            if (!CanTransition(previous.State, state)) throw new InvalidModuleTransitionException(module.Descriptor.Id, previous.State, state);
            var current = new ModuleHealth(module.Descriptor, state, isHealthy, detail, DateTimeOffset.UtcNow, previous.Revision + 1);
            healthByModuleId[module.Descriptor.Id] = current;
            transition = new ModuleHealthTransition(module.Descriptor.Id, previous.State, state, current.UpdatedAtUtc, detail);
        }
        Transitions.Add(1, new KeyValuePair<string, object?>("module.id", transition.ModuleId), new KeyValuePair<string, object?>("from", transition.From.ToString()), new KeyValuePair<string, object?>("to", transition.To.ToString()));
        StateChanged?.Invoke(transition);
    }

    private static bool CanTransition(ModuleLifecycleState from, ModuleLifecycleState to) => (from, to) switch
    {
        (ModuleLifecycleState.Registered, ModuleLifecycleState.Starting) => true,
        (ModuleLifecycleState.Starting, ModuleLifecycleState.Running or ModuleLifecycleState.Failed) => true,
        (ModuleLifecycleState.Running, ModuleLifecycleState.Degraded or ModuleLifecycleState.Draining or ModuleLifecycleState.Stopping or ModuleLifecycleState.Failed) => true,
        (ModuleLifecycleState.Degraded, ModuleLifecycleState.Running or ModuleLifecycleState.Draining or ModuleLifecycleState.Stopping or ModuleLifecycleState.Failed) => true,
        (ModuleLifecycleState.Draining, ModuleLifecycleState.Running or ModuleLifecycleState.Stopping or ModuleLifecycleState.Failed) => true,
        (ModuleLifecycleState.Stopping, ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed) => true,
        (ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed, ModuleLifecycleState.Starting) => true,
        _ => false
    };
}

public sealed record ModuleHealthTransition(string ModuleId, ModuleLifecycleState From, ModuleLifecycleState To, DateTimeOffset AtUtc, string? Detail);

public sealed class ModuleDependencyException(string message) : InvalidOperationException(message);

public sealed class InvalidModuleTransitionException(string moduleId, ModuleLifecycleState from, ModuleLifecycleState to)
    : InvalidOperationException($"Module '{moduleId}' cannot transition from {from} to {to}.");
