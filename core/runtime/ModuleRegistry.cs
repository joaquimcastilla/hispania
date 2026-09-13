using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly Dictionary<string, IModule> modules = new(StringComparer.Ordinal);

    public void Register(IModule registeredModule)
    {
        ArgumentNullException.ThrowIfNull(registeredModule);

        if (!modules.TryAdd(registeredModule.Descriptor.Id, registeredModule))
        {
            throw new DuplicateModuleIdException(registeredModule.Descriptor.Id);
        }
    }

    public IModule GetById(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        return modules.TryGetValue(moduleId, out var module)
            ? module
            : throw new ModuleNotFoundException(moduleId);
    }

    public IReadOnlyCollection<IModule> List() => modules.Values.ToArray();
}
