using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public interface IModuleRegistry
{
    void Register(IModule registeredModule);

    IModule GetById(string moduleId);

    IReadOnlyCollection<IModule> List();
}
