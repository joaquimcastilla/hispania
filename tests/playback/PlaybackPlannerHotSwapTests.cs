using System.Diagnostics;
using Hispania.Media.Domain.Identity;
using Hispania.Library.Application;
using Hispania.Playback.Conservative;
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
using System.Net.Http.Json;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class PlaybackPlannerHotSwapTests
{
    [Fact]
    public async Task PersistentExperimentRestoresStableAssignmentsAndRealPlannerTrafficAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-playback-experiment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "experiment-state.json");
        const string salt = "test-only-stable-assignment-salt-phase10";
        var catalog = new InMemoryMediaCatalog();
        var direct = new DirectPlayPlanner(catalog);
        var conservative = new ConservativePlaybackPlanner(catalog);
        var firstRegistry = new ModuleRegistry(); firstRegistry.Register(direct); firstRegistry.Register(conservative);
        var firstRuntime = new ModuleRuntime(firstRegistry);
        await firstRuntime.StartModuleAsync(direct.Descriptor.Id); await firstRuntime.StartModuleAsync(conservative.Descriptor.Id);
        var firstStore = new JsonPlaybackExperimentStore(statePath);
        var firstRouter = new VariantExperimentRouter<IPlaybackPlanner>(firstStore, salt);
        string[] cohort = [];
        Dictionary<string, VariantIdentity> beforeRestart = new(StringComparer.Ordinal);
        using (var firstService = new PlaybackExperimentService(firstRegistry, firstRuntime, firstRouter, firstStore, salt))
        {
            await firstService.StartAsync(CancellationToken.None);
            await firstService.ConfigureAsync(new PlaybackExperimentConfigurationRequest("persistent-planner", "TrafficSplit",
                [new(direct.Descriptor.Id, 7_000), new(conservative.Descriptor.Id, 3_000)],
                FallbackPolicy: "RedistributeToHealthy", Persistent: true));
            cohort = Enumerable.Range(0, 160).Select(index => $"restart-session-{index:D3}").ToArray();
            beforeRestart = cohort.ToDictionary(key => key, key =>
            { _ = firstRouter.Resolve(key, out var assignment); return assignment; });
            Parallel.ForEach(cohort, key => firstService.Plan(key, MediaEntityId.New(), direct, "before-restart"));
            Assert.Equal(2, firstService.Export("persistent-planner").Variants.Count);
            var persistedJson = File.ReadAllText(statePath);
            Assert.DoesNotContain("restart-session-", persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(salt, persistedJson, StringComparison.Ordinal);
        }

        var recoveredCatalog = new InMemoryMediaCatalog();
        var recoveredDirect = new DirectPlayPlanner(recoveredCatalog);
        var recoveredConservative = new ConservativePlaybackPlanner(recoveredCatalog);
        var recoveredRegistry = new ModuleRegistry(); recoveredRegistry.Register(recoveredDirect); recoveredRegistry.Register(recoveredConservative);
        var recoveredRuntime = new ModuleRuntime(recoveredRegistry);
        await recoveredRuntime.StartModuleAsync(recoveredDirect.Descriptor.Id); await recoveredRuntime.StartModuleAsync(recoveredConservative.Descriptor.Id);
        var recoveredStore = new JsonPlaybackExperimentStore(statePath);
        var recoveredRouter = new VariantExperimentRouter<IPlaybackPlanner>(recoveredStore, salt);
        using (var recoveredService = new PlaybackExperimentService(recoveredRegistry, recoveredRuntime, recoveredRouter, recoveredStore, salt))
        {
            await recoveredService.StartAsync(CancellationToken.None);
            Assert.Equal("Ready", recoveredService.RecoveryStatus.State);
            Assert.Equal(1, recoveredService.Snapshot!.Revision);
            Assert.Equal(VariantRoutingMode.TrafficSplit, recoveredService.Snapshot.Mode);
            foreach (var (key, expected) in beforeRestart)
            {
                _ = recoveredRouter.Resolve(key, out var actual);
                Assert.Equal(expected, actual);
            }
            Parallel.ForEach(cohort, key => recoveredService.Plan(key, MediaEntityId.New(), recoveredDirect, "after-restart"));
            var metrics = recoveredService.Export("persistent-planner").Variants;
            Assert.Equal(2, metrics.Count(item => item.Completed > 0));
            Assert.Equal(cohort.Length, metrics.Sum(item => item.Completed));
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task PersistentRecoveryFallsBackForMissingVariantsButRejectsChangedContracts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-experiment-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string salt = "test-only-stable-assignment-salt-phase10";
        var statePath = Path.Combine(directory, "experiment-state.json");
        var original = new PlannerModule("planner-recovery-a");
        var missing = new PlannerModule("planner-recovery-b", unavailable: true);
        var originalRegistry = new ModuleRegistry(); originalRegistry.Register(original); originalRegistry.Register(missing);
        var originalRuntime = new ModuleRuntime(originalRegistry);
        await originalRuntime.StartModuleAsync(original.Descriptor.Id); await originalRuntime.StartModuleAsync(missing.Descriptor.Id);
        var originalStore = new JsonPlaybackExperimentStore(statePath);
        using (var service = new PlaybackExperimentService(originalRegistry, originalRuntime,
            new VariantExperimentRouter<IPlaybackPlanner>(originalStore, salt), originalStore, salt))
        {
            await service.StartAsync(CancellationToken.None);
            await service.ConfigureAsync(new PlaybackExperimentConfigurationRequest("recoverable", "TrafficSplit",
                [new(original.Descriptor.Id, 6_000), new(missing.Descriptor.Id, 4_000)], Persistent: true));
        }

        var rotatedSaltStore = new JsonPlaybackExperimentStore(statePath);
        using (var rotatedSaltService = new PlaybackExperimentService(originalRegistry, originalRuntime,
            new VariantExperimentRouter<IPlaybackPlanner>(rotatedSaltStore, salt + "-rotated"), rotatedSaltStore, salt + "-rotated"))
        {
            await rotatedSaltService.StartAsync(CancellationToken.None);
            Assert.Equal("NotResumed", rotatedSaltService.RecoveryStatus.State);
            Assert.Null(rotatedSaltService.Snapshot);
        }

        var surviving = new PlannerModule(original.Descriptor.Id);
        var fallbackRegistry = new ModuleRegistry(); fallbackRegistry.Register(surviving);
        var fallbackRuntime = new ModuleRuntime(fallbackRegistry); await fallbackRuntime.StartModuleAsync(surviving.Descriptor.Id);
        var fallbackStore = new JsonPlaybackExperimentStore(statePath);
        using (var fallbackService = new PlaybackExperimentService(fallbackRegistry, fallbackRuntime,
            new VariantExperimentRouter<IPlaybackPlanner>(fallbackStore, salt), fallbackStore, salt))
        {
            await fallbackService.StartAsync(CancellationToken.None);
            Assert.Equal("RecoveredWithFallback", fallbackService.RecoveryStatus.State);
            Assert.Equal(VariantRoutingMode.Manual, fallbackService.Snapshot!.Mode);
            Assert.Single(fallbackService.Snapshot.Implementations);
            Assert.IsType<PlaybackMediaNotFound>(fallbackService.Plan("new-session", MediaEntityId.New(), surviving, "recovered-fallback"));
        }

        var incompatible = new PlannerModule(surviving.Descriptor.Id, contractVersion: new Version(2, 0));
        var incompatibleRegistry = new ModuleRegistry(); incompatibleRegistry.Register(incompatible);
        var incompatibleRuntime = new ModuleRuntime(incompatibleRegistry); await incompatibleRuntime.StartModuleAsync(incompatible.Descriptor.Id);
        var incompatibleStore = new JsonPlaybackExperimentStore(statePath);
        using var incompatibleService = new PlaybackExperimentService(incompatibleRegistry, incompatibleRuntime,
            new VariantExperimentRouter<IPlaybackPlanner>(incompatibleStore, salt), incompatibleStore, salt);
        await incompatibleService.StartAsync(CancellationToken.None);
        Assert.Equal("NotResumed", incompatibleService.RecoveryStatus.State);
        Assert.Null(incompatibleService.Snapshot);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task AdminConfiguredPersistentExperimentRecoversWhenTestServerHostRestarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-admin-experiment-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "state.json");
        const string salt = "test-only-stable-assignment-salt-phase10";
        const string token = "phase10-restart-admin-token";
        var firstSetup = await CreateRealPlannerSetupAsync();
        var firstStore = new JsonPlaybackExperimentStore(statePath);
        var firstRouter = new VariantExperimentRouter<IPlaybackPlanner>(firstStore, salt);
        using var firstExperiments = new PlaybackExperimentService(firstSetup.Registry, firstSetup.Runtime, firstRouter, firstStore, salt);
        await firstExperiments.StartAsync(CancellationToken.None);
        var (firstApp, firstClient) = await StartExperimentAdminHostAsync(firstSetup, firstExperiments, token, Path.Combine(directory, "extensions-one"));
        using (firstClient)
        {
            firstClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var request = new PlaybackExperimentConfigurationRequest("api-restart", "TrafficSplit",
                [new("hispania.playback.direct-planner", 7_000), new("hispania.playback.conservative-planner", 3_000)],
                FallbackPolicy: "RedistributeToHealthy", Persistent: true);
            using var configured = await firstClient.PostAsJsonAsync("/internal/admin/modules/playback-experiments", request);
            Assert.Equal(System.Net.HttpStatusCode.OK, configured.StatusCode);
            var sessions = Enumerable.Range(0, 120).Select(index => $"api-restart-session-{index:D3}").ToArray();
            var assignments = sessions.ToDictionary(key => key, key =>
            { _ = firstRouter.Resolve(key, out var assigned); return assigned; });
            Parallel.ForEach(sessions, key => firstExperiments.Plan(key, MediaEntityId.New(), firstSetup.Router.ActiveGeneration.Implementation, "before-host-restart"));
            Assert.Equal(2, firstExperiments.Export("api-restart").Variants.Count(item => item.Completed > 0));
            await firstApp.StopAsync();
            await firstApp.DisposeAsync();

            var secondSetup = await CreateRealPlannerSetupAsync();
            var secondStore = new JsonPlaybackExperimentStore(statePath);
            var secondRouter = new VariantExperimentRouter<IPlaybackPlanner>(secondStore, salt);
            using var secondExperiments = new PlaybackExperimentService(secondSetup.Registry, secondSetup.Runtime, secondRouter, secondStore, salt);
            await secondExperiments.StartAsync(CancellationToken.None);
            var (secondApp, secondClient) = await StartExperimentAdminHostAsync(secondSetup, secondExperiments, token, Path.Combine(directory, "extensions-two"));
            using (secondClient)
            {
                secondClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var status = await secondClient.GetAsync("/internal/admin/modules/playback-experiments");
                Assert.Equal(System.Net.HttpStatusCode.OK, status.StatusCode);
                using var statusDocument = System.Text.Json.JsonDocument.Parse(await status.Content.ReadAsStringAsync());
                Assert.Equal("Ready", statusDocument.RootElement.GetProperty("recovery").GetProperty("state").GetString());
                Assert.Equal(1, secondExperiments.Snapshot!.Revision);
                foreach (var (key, expected) in assignments)
                {
                    _ = secondRouter.Resolve(key, out var actual);
                    Assert.Equal(expected, actual);
                }
                Parallel.ForEach(sessions, key => secondExperiments.Plan(key, MediaEntityId.New(), secondSetup.Router.ActiveGeneration.Implementation, "after-host-restart"));
                Assert.Equal(2, secondExperiments.Export("api-restart").Variants.Count(item => item.Completed > 0));
                using var metrics = await secondClient.GetAsync("/internal/admin/modules/playback-experiments/api-restart/metrics");
                Assert.Equal(System.Net.HttpStatusCode.OK, metrics.StatusCode);
                using var finished = await secondClient.PostAsync("/internal/admin/modules/playback-experiments/api-restart/finish", content: null);
                Assert.Equal(System.Net.HttpStatusCode.NoContent, finished.StatusCode);
                await secondApp.StopAsync();
                await secondApp.DisposeAsync();
            }
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task RealPlannerModulesReceiveConfiguredTrafficAndStableCohortsSurviveWeightUpdates()
    {
        var catalog = new InMemoryMediaCatalog();
        var direct = new DirectPlayPlanner(catalog);
        var conservative = new ConservativePlaybackPlanner(catalog);
        var registry = new ModuleRegistry();
        registry.Register(direct); registry.Register(conservative);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync(direct.Descriptor.Id);
        await runtime.StartModuleAsync(conservative.Descriptor.Id);
        var router = new VariantExperimentRouter<IPlaybackPlanner>(pseudonymSalt: "phase10-test-salt");
        using var experiments = new PlaybackExperimentService(registry, runtime, router);
        await experiments.StartAsync(CancellationToken.None);

        await experiments.ConfigureAsync(new PlaybackExperimentConfigurationRequest("planner-live", "TrafficSplit",
        [new(direct.Descriptor.Id, 7_000), new(conservative.Descriptor.Id, 3_000)], FallbackPolicy: "RedistributeToHealthy"));

        const int cohortSize = 2_000;
        var existingSubjects = Enumerable.Range(0, cohortSize).Select(index => $"session-{index:D5}").ToArray();
        var selectedBefore = existingSubjects.ToDictionary(subject => subject, subject =>
        { _ = router.Resolve(subject, out var variant); return variant; });
        Parallel.ForEach(existingSubjects, subject => experiments.Plan(subject, MediaEntityId.New(), direct, Guid.NewGuid().ToString("N")));
        var beforeWeights = experiments.Export("planner-live").Variants.ToDictionary(item => item.Variant.ModuleId);
        Assert.InRange(beforeWeights[direct.Descriptor.Id].Completed, 1_300, 1_500);
        Assert.InRange(beforeWeights[conservative.Descriptor.Id].Completed, 500, 700);

        await experiments.ConfigureAsync(new PlaybackExperimentConfigurationRequest("planner-live", "TrafficSplit",
        [new(direct.Descriptor.Id, 1_000), new(conservative.Descriptor.Id, 9_000)], FallbackPolicy: "RedistributeToHealthy"));
        foreach (var subject in existingSubjects)
        {
            _ = router.Resolve(subject, out var retained);
            Assert.Equal(selectedBefore[subject], retained);
        }
        _ = router.Resolve("new-session-after-reweight", out var freshAssignment);
        Assert.Contains(freshAssignment, router.Snapshot!.Implementations.Keys);
        Assert.Equal(2, router.Snapshot!.Revision);

        await experiments.RetireVariantAsync("planner-live", conservative.Descriptor.Id);
        Assert.Single(router.Snapshot!.Implementations);
        Assert.Contains(experiments.Export("planner-live").Variants, item => item.Variant.ModuleId == conservative.Descriptor.Id && item.Completed > 0);
        var assignmentAfterRetirement = experiments.Plan("session-retired-cohort", MediaEntityId.New(), conservative, "retired-cohort");
        Assert.IsType<PlaybackMediaNotFound>(assignmentAfterRetirement);
    }

    [Fact]
    public async Task ConfigurationChangeKeepsAnActivePlanOnItsOriginalVariant()
    {
        var active = new PlannerModule("planner-active", block: true);
        var next = new PlannerModule("planner-next", unavailable: true);
        var registry = new ModuleRegistry(); registry.Register(active); registry.Register(next);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync(active.Descriptor.Id); await runtime.StartModuleAsync(next.Descriptor.Id);
        var router = new VariantExperimentRouter<IPlaybackPlanner>(pseudonymSalt: "pin-test");
        using var experiments = new PlaybackExperimentService(registry, runtime, router);
        await experiments.StartAsync(CancellationToken.None);
        await experiments.ConfigureAsync(new PlaybackExperimentConfigurationRequest("pin", "Manual",
            [new(active.Descriptor.Id), new(next.Descriptor.Id)], ActiveModuleId: active.Descriptor.Id));

        var operation = Task.Factory.StartNew(() => experiments.Plan("session-pinned", MediaEntityId.New(), next, "correlation-active"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await active.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await experiments.ConfigureAsync(new PlaybackExperimentConfigurationRequest("pin", "Manual",
            [new(active.Descriptor.Id), new(next.Descriptor.Id)], ActiveModuleId: next.Descriptor.Id));
        active.Release.SetResult();

        Assert.IsType<PlaybackMediaNotFound>(await operation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsType<DirectPlayUnavailable>(experiments.Plan("new-session", MediaEntityId.New(), active, "correlation-next"));
        Assert.Equal(1, active.Calls);
        Assert.Equal(1, next.Calls);
    }

    [Fact]
    public async Task ShadowFailureOrObserverTimeoutIsContainedAndOnlyTheActiveResultIsReturned()
    {
        var active = new PlannerModule("planner-shadow-active");
        var shadow = new PlannerModule("planner-shadow-secondary", throwOnPlan: true);
        var registry = new ModuleRegistry(); registry.Register(active); registry.Register(shadow);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync(active.Descriptor.Id); await runtime.StartModuleAsync(shadow.Descriptor.Id);
        var router = new VariantExperimentRouter<IPlaybackPlanner>(pseudonymSalt: "shadow-test");
        using var experiments = new PlaybackExperimentService(registry, runtime, router);
        await experiments.StartAsync(CancellationToken.None);
        await experiments.ConfigureAsync(new PlaybackExperimentConfigurationRequest("shadow", "Shadow",
            [new(active.Descriptor.Id), new(shadow.Descriptor.Id)], ActiveModuleId: active.Descriptor.Id, ShadowModuleId: shadow.Descriptor.Id));

        var activeResult = experiments.Plan("session-shadow", MediaEntityId.New(), shadow, "trace-shadow");
        Assert.IsType<PlaybackMediaNotFound>(activeResult);
        await WaitUntilAsync(() =>
        {
            var metrics = experiments.Export("shadow").Variants.Single(item => item.Variant.ModuleId == shadow.Descriptor.Id);
            return metrics.ShadowErrors + metrics.Timeouts == 1;
        }, TimeSpan.FromSeconds(3));
        var export = experiments.Export("shadow");
        var secondaryMetrics = Assert.Single(export.Variants, item => item.Variant.ModuleId == shadow.Descriptor.Id);
        Assert.Equal(1, secondaryMetrics.ShadowErrors + secondaryMetrics.Timeouts);
        Assert.Equal("trace-shadow", export.Variants.Single(item => item.Variant.ModuleId == active.Descriptor.Id).LastCorrelationId);
    }

    [Fact]
    public async Task HealthFailureFallsBackAndAutomaticThresholdDisablesTheFailedVariant()
    {
        var broken = new PlannerModule("planner-experiment-broken", throwOnPlan: true);
        var healthy = new PlannerModule("planner-experiment-healthy", unavailable: true);
        var registry = new ModuleRegistry(); registry.Register(broken); registry.Register(healthy);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync(broken.Descriptor.Id); await runtime.StartModuleAsync(healthy.Descriptor.Id);
        var router = new VariantExperimentRouter<IPlaybackPlanner>(pseudonymSalt: "fallback-test");
        using var experiments = new PlaybackExperimentService(registry, runtime, router);
        await experiments.StartAsync(CancellationToken.None);
        await experiments.ConfigureAsync(new PlaybackExperimentConfigurationRequest("fallback", "TrafficSplit",
            [new(broken.Descriptor.Id, 5_000), new(healthy.Descriptor.Id, 5_000)],
            FallbackPolicy: "RedistributeToHealthy", AutomaticDisableAfterErrors: 1));

        var failingSession = Enumerable.Range(0, 100).Select(index => $"session-{index}")
            .First(key => { _ = router.Resolve(key, out var assigned); return assigned.ModuleId == broken.Descriptor.Id; });
        Assert.Throws<InvalidOperationException>(() => experiments.Plan(failingSession, MediaEntityId.New(), healthy, "trace-failure"));
        var fallback = experiments.Plan(failingSession, MediaEntityId.New(), broken, "trace-fallback");
        Assert.IsType<DirectPlayUnavailable>(fallback);
        Assert.Equal(1, broken.Calls);
        var export = experiments.Export("fallback");
        Assert.Equal(1, Assert.Single(export.Variants, item => item.Variant.ModuleId == broken.Descriptor.Id).Fallbacks);
    }

    [Fact]
    public async Task ReplacementRoutesNewOperationsAndDrainsTheExistingGeneration()
    {
        var old = new PlannerModule("planner-old", block: true);
        var next = new PlannerModule("planner-next", unavailable: true);
        var setup = await SetupAsync(old, next);
        var operation = Task.Run(() => setup.Router.Plan(MediaEntityId.New()));
        await old.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var replacement = setup.Service.ReplaceAsync(next.Descriptor.Id);
        await WaitForStateAsync(setup.Runtime, old.Descriptor.Id, ModuleLifecycleState.Draining);
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
        old.ExpectCalls(activeCalls);
        var operations = Enumerable.Range(0, activeCalls).Select(_ => Task.Factory.StartNew(
            () => setup.Router.Plan(MediaEntityId.New()), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await old.ExpectedCallsEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var draining = CreateStateWaiter(setup.Runtime, old.Descriptor.Id, ModuleLifecycleState.Draining);
        var replacement = setup.Service.ReplaceAsync(next.Descriptor.Id);
        await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await WaitForStateAsync(setup.Runtime, broken.Descriptor.Id, ModuleLifecycleState.Stopped);
        Assert.Equal(old.Descriptor.Id, setup.Router.ActiveModuleId);
        Assert.Equal(ModuleLifecycleState.Running, setup.Runtime.GetHealth(old.Descriptor.Id).State);

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
        builder.Services.AddSingleton<Hispania.Platform.Contracts.Audit.IPlatformAuditWriter, InMemoryPlatformAuditWriter>();
        builder.Services.AddSingleton(setup.Runtime);
        var experimentRouter = new VariantExperimentRouter<IPlaybackPlanner>();
        var experiments = new PlaybackExperimentService(setup.Registry, setup.Runtime, experimentRouter);
        await experiments.StartAsync(CancellationToken.None);
        builder.Services.AddSingleton(experiments);
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
        using var unauthorizedExperiments = await client.GetAsync("/internal/admin/modules/playback-experiments");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorizedExperiments.StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-secret");
        using var authorized = await client.GetAsync("/internal/admin/modules");
        Assert.Equal(System.Net.HttpStatusCode.OK, authorized.StatusCode);
        using var authorizedExperiments = await client.GetAsync("/internal/admin/modules/playback-experiments");
        Assert.Equal(System.Net.HttpStatusCode.OK, authorizedExperiments.StatusCode);
        await setup.Runtime.StartModuleAsync(next.Descriptor.Id);
        var configuration = new PlaybackExperimentConfigurationRequest("admin-test", "TrafficSplit",
            [new(old.Descriptor.Id, 7_000), new(next.Descriptor.Id, 3_000)]);
        client.DefaultRequestHeaders.Authorization = null;
        using var unauthorizedConfigure = await client.PostAsJsonAsync("/internal/admin/modules/playback-experiments", configuration);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorizedConfigure.StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-secret");
        using var configured = await client.PostAsJsonAsync("/internal/admin/modules/playback-experiments", configuration);
        Assert.Equal(System.Net.HttpStatusCode.OK, configured.StatusCode);
        Parallel.For(0, 500, index => experiments.Plan($"api-session-{index:D4}", MediaEntityId.New(), old, $"api-{index}"));
        Assert.Equal(2, experiments.Export("admin-test").Variants.Count(item => item.Completed > 0));
        using var exportedMetrics = await client.GetAsync("/internal/admin/modules/playback-experiments/admin-test/metrics");
        Assert.Equal(System.Net.HttpStatusCode.OK, exportedMetrics.StatusCode);
        var failedVariantSession = Enumerable.Range(0, 500).Select(index => $"suspend-session-{index:D4}")
            .First(key => { _ = experimentRouter.Resolve(key, out var assigned); return assigned.ModuleId == next.Descriptor.Id; });
        using var suspended = await client.PostAsync($"/internal/admin/modules/playback-experiments/admin-test/variants/{next.Descriptor.Id}/suspend", content: null);
        Assert.Equal(System.Net.HttpStatusCode.OK, suspended.StatusCode);
        Assert.IsType<PlaybackMediaNotFound>(experiments.Plan(failedVariantSession, MediaEntityId.New(), old, "after-variant-suspend"));
        Assert.True(experiments.Export("admin-test").Variants.Single(item => item.Variant.ModuleId == next.Descriptor.Id).Fallbacks > 0);
        using var rollback = await client.PostAsync("/internal/admin/modules/playback-experiments/admin-test/rollback", content: null);
        Assert.Equal(System.Net.HttpStatusCode.OK, rollback.StatusCode);
        Assert.Equal(VariantRoutingMode.TrafficSplit, experiments.Snapshot!.Mode);
        Assert.IsType<DirectPlayUnavailable>(experiments.Plan(failedVariantSession, MediaEntityId.New(), old, "after-rollback"));
        using var finished = await client.PostAsync("/internal/admin/modules/playback-experiments/admin-test/finish", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, finished.StatusCode);
        using var archivedMetrics = await client.GetAsync("/internal/admin/modules/playback-experiments/admin-test/metrics");
        Assert.Equal(System.Net.HttpStatusCode.OK, archivedMetrics.StatusCode);
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

    private static async Task<TestSetup> CreateRealPlannerSetupAsync()
    {
        var catalog = new InMemoryMediaCatalog();
        var direct = new DirectPlayPlanner(catalog);
        var conservative = new ConservativePlaybackPlanner(catalog);
        var registry = new ModuleRegistry(); registry.Register(direct); registry.Register(conservative);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync(direct.Descriptor.Id); await runtime.StartModuleAsync(conservative.Descriptor.Id);
        var router = new HotSwappablePlaybackPlanner(direct.Descriptor.Id, direct);
        return new TestSetup(registry, runtime, router, new PlaybackPlannerHotSwapService(registry, runtime, router));
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartExperimentAdminHostAsync(TestSetup setup,
        PlaybackExperimentService experiments, string token, string extensionsPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:AdminToken"] = token });
        builder.Services.AddSingleton<IModuleRuntime>(setup.Runtime);
        builder.Services.AddSingleton<Hispania.Platform.Contracts.Audit.IPlatformAuditWriter, InMemoryPlatformAuditWriter>();
        builder.Services.AddSingleton(setup.Runtime);
        builder.Services.AddSingleton(experiments);
        builder.Services.AddSingleton(setup.Router);
        builder.Services.AddSingleton(setup.Service);
        builder.Services.AddSingleton(new ModuleExtensionCatalog(extensionsPath, new Version(1, 0, 0), setup.Runtime,
            setup.Registry, new Dictionary<string, Func<IModule>>()));
        var app = builder.Build();
        app.MapModuleAdminEndpoints();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static Task WaitForStateAsync(ModuleRuntime runtime, string moduleId, ModuleLifecycleState state)
    {
        if (runtime.GetHealth(moduleId).State == state) return Task.CompletedTask;
        return CreateStateWaiter(runtime, moduleId, state).Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout) throw new TimeoutException("The expected asynchronous condition was not reached.");
            await Task.Delay(1);
        }
    }

    private static TaskCompletionSource CreateStateWaiter(ModuleRuntime runtime, string moduleId, ModuleLifecycleState state)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTransition(ModuleHealthTransition transition)
        {
            if (transition.ModuleId == moduleId && transition.To == state)
            {
                runtime.StateChanged -= OnTransition;
                completion.TrySetResult();
            }
        }
        runtime.StateChanged += OnTransition;
        if (runtime.GetHealth(moduleId).State == state)
        {
            runtime.StateChanged -= OnTransition;
            completion.TrySetResult();
        }
        _ = completion.Task.ContinueWith(_ => runtime.StateChanged -= OnTransition, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion;
    }

    private sealed record TestSetup(ModuleRegistry Registry, ModuleRuntime Runtime, HotSwappablePlaybackPlanner Router, PlaybackPlannerHotSwapService Service);

    private sealed class PlannerModule : IModule, IModuleHealthCheck, IModuleExperimentDeclaration, IPlaybackPlanner
    {
        private readonly bool block;
        private readonly bool throwOnPlan;
        private readonly bool healthy;
        private readonly bool unavailable;
        private int expectedCalls;
        private TaskCompletionSource expectedCallsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public IReadOnlyCollection<ModuleExperimentCapability> ExperimentCapabilities { get; } =
        [new("hispania.playback.planner", new Version(1, 0), [ModuleExperimentMode.Manual, ModuleExperimentMode.Shadow, ModuleExperimentMode.TrafficSplit], "Hispania.Library.Contracts.IMediaCatalogReader@1.0")];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task ExpectedCallsEntered => expectedCallsEntered.Task;
        public void ExpectCalls(int count)
        {
            expectedCalls = count;
            if (Calls >= count) expectedCallsEntered.TrySetResult();
        }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(healthy);

        public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId)
        {
            var callCount = Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            if (callCount >= Volatile.Read(ref expectedCalls)) expectedCallsEntered.TrySetResult();
            if (block)
            {
                Release.Task.GetAwaiter().GetResult();
            }
            if (throwOnPlan) throw new InvalidOperationException("Simulated planner failure.");
            return unavailable ? new DirectPlayUnavailable(mediaEntityId) : new PlaybackMediaNotFound(mediaEntityId);
        }
    }
}
