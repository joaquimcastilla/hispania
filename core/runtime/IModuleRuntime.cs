namespace Hispania.Runtime;

public interface IModuleRuntime
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    ModuleHealth GetHealth(string moduleId);

    IReadOnlyCollection<ModuleHealth> ListHealth();

    Task StartModuleAsync(string moduleId, CancellationToken cancellationToken = default);

    Task RegisterAndStartAsync(Hispania.ModuleSystem.Contracts.IModule moduleInstance, CancellationToken cancellationToken = default);

    void UnregisterStoppedModule(string moduleId);

    void RegisterStartedModule(Hispania.ModuleSystem.Contracts.IModule moduleInstance);

    Task DrainModuleAsync(string moduleId, CancellationToken cancellationToken = default);

    Task StopModuleAsync(string moduleId, CancellationToken cancellationToken = default);

    Task ResumeModuleAsync(string moduleId, CancellationToken cancellationToken = default);

    ValueTask<bool> CheckHealthAsync(string moduleId, CancellationToken cancellationToken = default);

    Task MarkDegradedAsync(string moduleId, string detail, CancellationToken cancellationToken = default);
}
