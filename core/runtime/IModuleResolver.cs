namespace Hispania.Runtime;

public interface IModuleResolver
{
    TContract Resolve<TContract>(string moduleId)
        where TContract : class;
}
