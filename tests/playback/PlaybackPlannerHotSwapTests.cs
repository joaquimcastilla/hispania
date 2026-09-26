using Hispania.Media.Domain.Identity;
using Hispania.ModuleSystem.Contracts;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Hispania.Runtime;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class PlaybackPlannerHotSwapTests
{
    [Fact]
    public async Task ReplacementRoutesNewOperationsAndDrainsTheExistingGeneration()
    {
        var old = new PlannerModule("planner-old", block: true);
        var next = new PlannerModule("planner-next", unavailable: true);
        var setup = await SetupAsync(old, next);
        var operation = Task.Run(() => setup.Router.Plan(MediaEntityId.New()));
        await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var replacement = setup.Service.ReplaceAsync(next.Descriptor.Id);
        await WaitUntilAsync(() => setup.Runtime.GetHealth(old.Descriptor.Id).State == ModuleLifecycleState.Draining);
        Assert.False(replacement.IsCompleted);
        Assert.IsType<DirectPlayUnavailable>(setup.Router.Plan(MediaEntityId.New()));
        Assert.Equal(1, old.Calls);
        Assert.Equal(1, next.Calls);

        old.Release.SetResult();
        Assert.IsType<PlaybackMediaNotFound>(await operation.WaitAsync(TimeSpan.FromSeconds(3)));
        var result = await replacement.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(old.Descriptor.Id, result.PreviousModuleId);
        Assert.Equal(next.Descriptor.Id, setup.Router.ActiveModuleId);
        Assert.Equal(ModuleLifecycleState.Stopped, setup.Runtime.GetHealth(old.Descriptor.Id).State);
    }

    [Fact]
    public async Task ReplacementDrainsAllConcurrentOperationsAndPinsEachToItsStartingGeneration()
    {
        const int activeCalls = 16;
        var old = new PlannerModule("planner-old", block: true);
        var next = new PlannerModule("planner-next", unavailable: true);
        var setup = await SetupAsync(old, next);
        var operations = Enumerable.Range(0, activeCalls).Select(_ => Task.Run(() => setup.Router.Plan(MediaEntityId.New()))).ToArray();
        await WaitUntilAsync(() => old.Calls == activeCalls);

        var replacement = setup.Service.ReplaceAsync(next.Descriptor.Id);
        await WaitUntilAsync(() => setup.Runtime.GetHealth(old.Descriptor.Id).State == ModuleLifecycleState.Draining);
        Assert.False(replacement.IsCompleted);
        for (var index = 0; index < 8; index++)
            Assert.IsType<DirectPlayUnavailable>(setup.Router.Plan(MediaEntityId.New()));
        Assert.Equal(activeCalls, old.Calls);
        Assert.Equal(8, next.Calls);

        old.Release.SetResult();
        var completed = await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(completed, result => Assert.IsType<PlaybackMediaNotFound>(result));
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(activeCalls, old.Calls);
        Assert.Equal(8, next.Calls);
    }

    [Fact]
    public async Task ThrowingReplacementIsNotRetriedAndAutomaticallyRollsBackForFutureOperations()
    {
        var old = new PlannerModule("planner-old");
        var broken = new PlannerModule("planner-broken", throwOnPlan: true);
        var setup = await SetupAsync(old, broken);
        await setup.Service.ReplaceAsync(broken.Descriptor.Id);

        Assert.Throws<InvalidOperationException>(() => setup.Router.Plan(MediaEntityId.New()));
        await WaitUntilAsync(() => setup.Router.ActiveModuleId == old.Descriptor.Id &&
            setup.Runtime.GetHealth(old.Descriptor.Id).State == ModuleLifecycleState.Running &&
            setup.Runtime.GetHealth(broken.Descriptor.Id).State == ModuleLifecycleState.Stopped);

        Assert.Equal(1, broken.Calls);
        Assert.IsType<PlaybackMediaNotFound>(setup.Router.Plan(MediaEntityId.New()));
        Assert.Equal(1, old.Calls);
        Assert.Equal(ModuleLifecycleState.Stopped, setup.Runtime.GetHealth(broken.Descriptor.Id).State);
    }

    [Fact]
    public async Task FailedHealthOrIncompatibleDescriptorDoesNotChangeTheActiveRoute()
    {
        var old = new PlannerModule("planner-old");
        var unhealthy = new PlannerModule("planner-unhealthy", healthy: false);
        var incompatible = new PlannerModule("planner-incompatible", dynamic: false);
        var incompatibleVersion = new PlannerModule("planner-incompatible-version", contractVersion: new Version(2, 0));
        var setup = await SetupAsync(old, unhealthy, incompatible, incompatibleVersion);

        await Assert.ThrowsAsync<ModuleStartupException>(() => setup.Service.ReplaceAsync(unhealthy.Descriptor.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReplaceAsync(incompatible.Descriptor.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReplaceAsync(incompatibleVersion.Descriptor.Id));
        Assert.Equal(old.Descriptor.Id, setup.Router.ActiveModuleId);
        Assert.Equal(ModuleLifecycleState.Running, setup.Runtime.GetHealth(old.Descriptor.Id).State);
    }

    [Fact]
    public async Task InternalAdminModuleApiRequiresConfiguredBearerAuthentication()
    {
        var old = new PlannerModule("planner-old");
        var next = new PlannerModule("planner-next", unavailable: true);
        var setup = await SetupAsync(old, next);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:AdminToken"] = "test-secret" });
        builder.Services.AddSingleton<IModuleRuntime>(setup.Runtime);
        builder.Services.AddSingleton(setup.Router);
        builder.Services.AddSingleton(setup.Service);
        builder.Services.AddSingleton(new ModuleExtensionCatalog(
            Path.Combine(Path.GetTempPath(), "hispania-admin-test-" + Guid.NewGuid().ToString("N")),
            new Version(1, 0, 0), setup.Runtime, setup.Registry, new Dictionary<string, Func<IModule>>()));
        await using var app = builder.Build();
        app.MapModuleAdminEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var unauthorized = await client.GetAsync("/internal/admin/modules");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-secret");
        using var authorized = await client.GetAsync("/internal/admin/modules");
        Assert.Equal(System.Net.HttpStatusCode.OK, authorized.StatusCode);
    }

    private static async Task<TestSetup> SetupAsync(params PlannerModule[] modules)
    {
        var registry = new ModuleRegistry();
        foreach (var module in modules) registry.Register(module);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync(modules[0].Descriptor.Id);
        var initial = modules[0];
        var router = new HotSwappablePlaybackPlanner(initial.Descriptor.Id, initial);
        var service = new PlaybackPlannerHotSwapService(registry, runtime, router);
        return new TestSetup(registry, runtime, router, service);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Expected module transition did not occur.");
            await Task.Delay(10);
        }
    }

    private sealed record TestSetup(ModuleRegistry Registry, ModuleRuntime Runtime, HotSwappablePlaybackPlanner Router, PlaybackPlannerHotSwapService Service);

    private sealed class PlannerModule : IModule, IModuleHealthCheck, IPlaybackPlanner
    {
        private readonly bool block;
        private readonly bool throwOnPlan;
        private readonly bool healthy;
        private readonly bool unavailable;

        public PlannerModule(string id, bool block = false, bool throwOnPlan = false, bool healthy = true, bool dynamic = true, bool unavailable = false, Version? contractVersion = null)
        {
            this.block = block;
            this.throwOnPlan = throwOnPlan;
            this.healthy = healthy;
            this.unavailable = unavailable;
            Descriptor = new ModuleDescriptor(id, id, new Version(1, 0, 0),
                [new ModuleCapability("hispania.playback.planner", contractVersion ?? new Version(1, 0), dynamic)]);
        }

        public ModuleDescriptor Descriptor { get; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(healthy);

        public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId)
        {
            Interlocked.Increment(ref calls);
            if (block)
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            if (throwOnPlan) throw new InvalidOperationException("Simulated planner failure.");
            return unavailable ? new DirectPlayUnavailable(mediaEntityId) : new PlaybackMediaNotFound(mediaEntityId);
        }
    }
}
