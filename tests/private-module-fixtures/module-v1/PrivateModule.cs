using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.PrivateFixture.Dependency;

namespace Hispania.PrivateFixture.ModuleV1;

public sealed class PrivateModule : IModule, IExtensionDemoV1
{
    public ModuleDescriptor Descriptor { get; } = new("hispania.test.private.v1", "Private dependency fixture v1", new Version(1, 0, 0),
        [new ModuleCapability("hispania.test.private", new Version(1, 0))]);

    public string GetMessage() => PrivateDependency.GetValue();
    public Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken) => Task.FromResult(GetMessage());
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = PrivateDependency.GetValue();
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
