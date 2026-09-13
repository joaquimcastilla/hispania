namespace Hispania.Runtime;

public sealed class ModuleResolver(IModuleRegistry registry) : IModuleResolver
{
    public TContract Resolve<TContract>(string moduleId)
        where TContract : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        var module = registry.GetById(moduleId);

        return module as TContract
            ?? throw new ModuleContractNotProvidedException(moduleId, typeof(TContract));
    }
}
