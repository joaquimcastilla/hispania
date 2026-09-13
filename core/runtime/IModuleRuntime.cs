namespace Hispania.Runtime;

public interface IModuleRuntime
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    ModuleHealth GetHealth(string moduleId);

    IReadOnlyCollection<ModuleHealth> ListHealth();
}
