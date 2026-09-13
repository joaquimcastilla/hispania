using Hispania.ModuleSystem.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.ArchitectureTest.Modules;

public static class ArchitectureTestModuleServiceCollectionExtensions
{
    public static IServiceCollection AddArchitectureTestModules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModule, ArchitectureTestModuleA>();
        services.AddSingleton<IModule, ArchitectureTestModuleB>();

        return services;
    }
}
