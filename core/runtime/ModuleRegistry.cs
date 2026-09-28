using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly Dictionary<string, IModule> modules = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();

    public void Register(IModule registeredModule)
    {
        ArgumentNullException.ThrowIfNull(registeredModule);

        lock (syncRoot)
        {
            if (!modules.TryAdd(registeredModule.Descriptor.Id, registeredModule)) throw new DuplicateModuleIdException(registeredModule.Descriptor.Id);
        }
    }

    public void Unregister(string moduleId)
    {
        lock (syncRoot)
            if (!modules.Remove(moduleId)) throw new ModuleNotFoundException(moduleId);
    }

    public IModule GetById(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        lock (syncRoot) return modules.TryGetValue(moduleId, out var module)
            ? module
            : throw new ModuleNotFoundException(moduleId);
    }

    public IReadOnlyCollection<IModule> List() { lock (syncRoot) return modules.Values.ToArray(); }
}
