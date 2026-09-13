using Hispania.ArchitectureTest.Contracts;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.ArchitectureTest.Modules;

public sealed class ArchitectureTestModuleA : IModule, IArchitectureDemonstrationService
{
    public ModuleDescriptor Descriptor { get; } = new(
        "hispania.architecture-test.a",
        "Architecture Test Module A",
        new Version(1, 0, 0));

    public string GetResponse() => "Response from Architecture Test Module A.";

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
