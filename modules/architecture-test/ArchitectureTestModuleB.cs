using Hispania.ArchitectureTest.Contracts;
using Hispania.Kernel;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.ArchitectureTest.Modules;

public sealed class ArchitectureTestModuleB : IModule, IArchitectureDemonstrationService
{
    public ModuleDescriptor Descriptor { get; } = new(
        "hispania.architecture-test.b",
        "Architecture Test Module B",
        new Version(1, 0, 0));

    public string GetResponse() => "Response from Architecture Test Module B.";

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
