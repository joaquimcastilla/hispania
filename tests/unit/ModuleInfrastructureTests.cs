using Hispania.ArchitectureTest.Contracts;
using Hispania.ArchitectureTest.Modules;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class ModuleInfrastructureTests
{
    [Fact]
    public void RegistryRegistersModuleA()
    {
        var registry = new ModuleRegistry();
        var module = new ArchitectureTestModuleA();

        registry.Register(module);

        Assert.Same(module, registry.GetById(module.Descriptor.Id));
    }

    [Fact]
    public void RegistryRegistersModuleB()
    {
        var registry = new ModuleRegistry();
        var module = new ArchitectureTestModuleB();

        registry.Register(module);

        Assert.Same(module, registry.GetById(module.Descriptor.Id));
    }

    [Fact]
    public void RegistryRejectsDuplicateModuleIds()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule("hispania.test.duplicate"));

        Assert.Throws<DuplicateModuleIdException>(() => registry.Register(new TestModule("hispania.test.duplicate")));
    }

    [Fact]
    public void ConfigurationSelectsModuleA()
    {
        var resolver = CreateResolver();
        var configuration = CreateConfiguration("hispania.architecture-test.a");

        var service = resolver.Resolve<IArchitectureDemonstrationService>(
            configuration["Modules:ArchitectureDemonstration"]!);

        Assert.Equal("Response from Architecture Test Module A.", service.GetResponse());
    }

    [Fact]
    public void ConfigurationSelectsModuleB()
    {
        var resolver = CreateResolver();
        var configuration = CreateConfiguration("hispania.architecture-test.b");

        var service = resolver.Resolve<IArchitectureDemonstrationService>(
            configuration["Modules:ArchitectureDemonstration"]!);

        Assert.Equal("Response from Architecture Test Module B.", service.GetResponse());
    }

    [Fact]
    public void ResolverFailsForAnUnknownModuleId()
    {
        var resolver = CreateResolver();

        Assert.Throws<ModuleNotFoundException>(() =>
            resolver.Resolve<IArchitectureDemonstrationService>("hispania.architecture-test.unknown"));
    }

    [Fact]
    public void ResolverFailsWhenModuleDoesNotProvideRequestedContract()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule("hispania.test.no-contract"));
        var resolver = new ModuleResolver(registry);

        Assert.Throws<ModuleContractNotProvidedException>(() =>
            resolver.Resolve<IArchitectureDemonstrationService>("hispania.test.no-contract"));
    }

    [Fact]
    public async Task RuntimeTracksTheExpectedLifecycle()
    {
        var module = new TestModule("hispania.test.lifecycle");
        var runtime = CreateRuntime(module);

        Assert.Equal(ModuleLifecycleState.Registered, runtime.GetHealth(module.Descriptor.Id).State);

        await runtime.StartAsync(CancellationToken.None);
        Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.True(runtime.GetHealth(module.Descriptor.Id).IsHealthy);

        await runtime.StopAsync(CancellationToken.None);
        Assert.Equal(ModuleLifecycleState.Stopped, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.False(runtime.GetHealth(module.Descriptor.Id).IsHealthy);
    }

    [Fact]
    public async Task StartupFailureLeavesTheModuleInFailedState()
    {
        var module = new TestModule("hispania.test.startup-failure", failOnStart: true);
        var runtime = CreateRuntime(module);

        await Assert.ThrowsAsync<ModuleStartupException>(() => runtime.StartAsync(CancellationToken.None));

        var health = runtime.GetHealth(module.Descriptor.Id);
        Assert.Equal(ModuleLifecycleState.Failed, health.State);
        Assert.False(health.IsHealthy);
        Assert.NotNull(health.Detail);
    }

    [Fact]
    public void ConsumerDependsOnlyOnTheArchitectureTestContract()
    {
        var resolver = CreateResolver();
        var consumer = new ContractOnlyConsumer(resolver.Resolve<IArchitectureDemonstrationService>("hispania.architecture-test.a"));

        Assert.Equal("Response from Architecture Test Module A.", consumer.Execute());
    }

    [Fact]
    public void RegistryRuntimeAndResolverHaveSeparateResponsibilities()
    {
        var module = new TestModule("hispania.test.separation");
        var registry = new ModuleRegistry();
        registry.Register(module);
        var runtime = new ModuleRuntime(registry);
        var resolver = new ModuleResolver(registry);

        Assert.Single(registry.List());
        Assert.Equal(ModuleLifecycleState.Registered, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.Throws<ModuleContractNotProvidedException>(() =>
            resolver.Resolve<IArchitectureDemonstrationService>(module.Descriptor.Id));
    }

    private static IConfiguration CreateConfiguration(string selectedModuleId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:ArchitectureDemonstration"] = selectedModuleId
            })
            .Build();

    private static ModuleResolver CreateResolver()
    {
        var registry = new ModuleRegistry();
        registry.Register(new ArchitectureTestModuleA());
        registry.Register(new ArchitectureTestModuleB());
        return new ModuleResolver(registry);
    }

    private static ModuleRuntime CreateRuntime(IModule module)
    {
        var registry = new ModuleRegistry();
        registry.Register(module);
        return new ModuleRuntime(registry);
    }

    private sealed class ContractOnlyConsumer(IArchitectureDemonstrationService service)
    {
        public string Execute() => service.GetResponse();
    }

    private sealed class TestModule : IModule
    {
        private readonly bool failOnStart;

        public TestModule(string id, bool failOnStart = false)
        {
            Descriptor = new ModuleDescriptor(id, "Test module", new Version(1, 0, 0));
            this.failOnStart = failOnStart;
        }

        public ModuleDescriptor Descriptor { get; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (failOnStart)
            {
                throw new InvalidOperationException("Expected startup failure.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
