using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.ExtensionDemo;

// TEST ONLY: neutral package graph fixtures, excluded from production distributions.
public sealed class PackageBaseModule : IModule, IExtensionDemoV1
{
    public ModuleDescriptor Descriptor { get; } = new("test.package.base", "TEST ONLY package base", new Version(1, 0, 0),
        [new ModuleCapability("test.package.base", new Version(1, 0))]);
    public string GetMessage() => "base";
    public async Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken)
    { await Task.Delay(delayMilliseconds, cancellationToken); return GetMessage(); }
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class PackageConsumerModule : IModule, IExtensionDemoV1
{
    public ModuleDescriptor Descriptor { get; } = new("test.package.consumer", "TEST ONLY package consumer", new Version(1, 0, 0),
        [new ModuleCapability("test.package.consumer", new Version(1, 0))]);
    public string GetMessage() => "consumer";
    public async Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken)
    { await Task.Delay(delayMilliseconds, cancellationToken); return GetMessage(); }
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class UnhealthyPackageConsumerModule : IModule, IExtensionDemoV1, IModuleHealthCheck
{
    public ModuleDescriptor Descriptor { get; } = new("test.package.consumer", "TEST ONLY package consumer", new Version(1, 0, 0),
        [new ModuleCapability("test.package.consumer", new Version(1, 0))]);
    public string GetMessage() => "unhealthy";
    public Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken) => Task.FromResult(GetMessage());
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
