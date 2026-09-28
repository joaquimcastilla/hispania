using Hispania.ModuleSystem.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Runtime;

public static class ModuleRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddModuleRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModuleRegistry>(serviceProvider =>
        {
            var registry = new ModuleRegistry();

            foreach (var registeredModule in serviceProvider.GetServices<IModule>())
            {
                registry.Register(registeredModule);
            }

            return registry;
        });
        services.AddSingleton<ModuleRuntime>();
        services.AddSingleton<IModuleRuntime>(serviceProvider => serviceProvider.GetRequiredService<ModuleRuntime>());
        services.AddSingleton<IModuleResolver, ModuleResolver>();
        services.AddSingleton<ICapabilityResolver, CapabilityResolver>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ModuleRuntime>());

        return services;
    }
}
