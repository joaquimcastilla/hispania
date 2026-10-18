using Hispania.ModuleSystem.Contracts;

namespace Hispania.Slice7.Fixture;

/// <summary>Original HISPANIA test fixture. It has no capabilities, permissions, I/O, or external dependencies.</summary>
public sealed class Slice7FixtureModule : IModule
{
    public ModuleDescriptor Descriptor { get; } = new("hispania.slice7.fixture", "HISPANIA Slice 7 Fixture", new Version(1, 0, 0));

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
