using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.ExtensionDemo;

/// <summary>Independent sample extension; it references only public module and demo contracts.</summary>
public sealed class IndependentGreetingModule : IModule, IExtensionDemoV1
{
    public ModuleDescriptor Descriptor { get; } = new(
        "hispania.extension-demo.greeting", "Independent greeting extension", new Version(1, 0, 0),
        [new ModuleCapability("hispania.extension-demo.greeting", new Version(1, 0))]);

    public string GetMessage() => "Hello from an independently implemented extension.";
    public Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(GetMessage()); }
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Compatible next package generation. The environment switch is only a deterministic acceptance-test fault injection.</summary>
public sealed class IndependentGreetingModuleV2 : IModule, IExtensionDemoV1
{
    public ModuleDescriptor Descriptor { get; } = new(
        "hispania.extension-demo.greeting", "Independent greeting extension", new Version(2, 0, 0),
        [new ModuleCapability("hispania.extension-demo.greeting", new Version(1, 0))]);

    public string GetMessage() => "Hello from greeting extension version 2.";
    public Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(GetMessage()); }
    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        Environment.GetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_V2_FAIL_START") == "1"
            ? ValueTask.FromException(new InvalidOperationException("Simulated greeting extension activation failure."))
            : ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Out-of-process greeting implementation used by the generic module host acceptance path.</summary>
public sealed class OutOfProcessGreetingModule : IModule, IExtensionDemoV1
{
    public ModuleDescriptor Descriptor { get; } = new(
        "hispania.extension-demo.greeting", "Independent greeting extension", new Version(3, 0, 0),
        [new ModuleCapability("hispania.extension-demo.greeting", new Version(1, 0))]);

    public string GetMessage()
    {
        var crashMarker = Environment.GetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH_MARKER");
        if (!string.IsNullOrWhiteSpace(crashMarker) && File.Exists(crashMarker)) Environment.Exit(23);
        return "Hello from a separately hosted greeting extension.";
    }

    public async Task<string> WaitForMessageAsync(int delayMilliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(delayMilliseconds, cancellationToken);
        return GetMessage();
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
