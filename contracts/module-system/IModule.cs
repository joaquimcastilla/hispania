using Hispania.Kernel;

namespace Hispania.ModuleSystem.Contracts;

public interface IModule
{
    ModuleDescriptor Descriptor { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}
