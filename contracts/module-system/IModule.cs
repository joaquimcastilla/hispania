namespace Hispania.ModuleSystem.Contracts;

public interface IModule
{
    ModuleDescriptor Descriptor { get; }

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Exposes typed public-contract proxies for a module whose implementation is remote.</summary>
public interface IModuleCapabilityProxyFactory
{
    object CreateCapabilityProxy(Type contractType);
}
