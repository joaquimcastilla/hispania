using System.Diagnostics.Metrics;
using Hispania.ModuleSystem.Contracts;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Hispania.Runtime;

namespace Hispania.Server;

public sealed record PlaybackPlannerSwapResult(string PreviousModuleId, string ActiveModuleId, DateTimeOffset ActivatedAtUtc);

/// <summary>Coordinates controlled in-process replacement of the read-only Playback Planner capability.</summary>
public sealed class PlaybackPlannerHotSwapService : IDisposable
{
    private const string ContractId = "hispania.playback.planner";
    private static readonly Version ContractVersion = new(1, 0);
    private static readonly Meter Meter = new("Hispania.Server.ModuleHotSwap", "1.0");
    private static readonly Counter<long> Replacements = Meter.CreateCounter<long>("hispania.modules.hot_swap.replacements");
    private static readonly Counter<long> Rollbacks = Meter.CreateCounter<long>("hispania.modules.hot_swap.rollbacks");
    private readonly IModuleRegistry registry;
    private readonly IModuleRuntime runtime;
    private readonly HotSwappablePlaybackPlanner router;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private PlannerGeneration? rollbackGeneration;
    private string? rollbackModuleId;

    public PlaybackPlannerHotSwapService(IModuleRegistry registry, IModuleRuntime runtime, HotSwappablePlaybackPlanner router)
    {
        this.registry = registry;
        this.runtime = runtime;
        this.router = router;
        router.OperationFailed += OnOperationFailed;
    }

    public async Task<PlaybackPlannerSwapResult> ReplaceAsync(string targetModuleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetModuleId);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousModuleId = router.ActiveModuleId;
            if (previousModuleId == targetModuleId) throw new InvalidOperationException("The requested playback planner is already active.");
            var target = registry.GetById(targetModuleId);
            var capability = target.Descriptor.Provides.SingleOrDefault(item => item.ContractId == ContractId && item.Version == ContractVersion);
            if (capability is null || !capability.DynamicallyReplaceable || target is not IPlaybackPlanner planner || target is not IModuleHealthCheck)
                throw new InvalidOperationException($"Module '{targetModuleId}' is not a health-checked, dynamically replaceable provider of {ContractId}@{ContractVersion}.");

            var targetState = runtime.GetHealth(targetModuleId).State;
            if (targetState is ModuleLifecycleState.Registered or ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed)
                await runtime.StartModuleAsync(targetModuleId, cancellationToken).ConfigureAwait(false);
            if (!await runtime.CheckHealthAsync(targetModuleId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Module '{targetModuleId}' failed its pre-activation health check.");

            var previousGeneration = router.Replace(targetModuleId, planner);
            try
            {
                await runtime.DrainModuleAsync(previousModuleId, cancellationToken).ConfigureAwait(false);
                await previousGeneration.WaitForDrainAsync(cancellationToken).ConfigureAwait(false);
                await runtime.StopModuleAsync(previousModuleId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception activationFailure)
            {
                await RestoreAfterFailedActivationAsync(previousGeneration, targetModuleId).ConfigureAwait(false);
                throw new InvalidOperationException("Playback planner replacement could not finish draining the previous implementation; the prior route was restored when possible.", activationFailure);
            }

            rollbackGeneration = previousGeneration;
            rollbackModuleId = previousModuleId;
            Replacements.Add(1, new KeyValuePair<string, object?>("from", previousModuleId), new KeyValuePair<string, object?>("to", targetModuleId));
            return new PlaybackPlannerSwapResult(previousModuleId, targetModuleId, DateTimeOffset.UtcNow);
        }
        finally { lifecycleGate.Release(); }
    }

    private void OnOperationFailed(PlannerGeneration failedGeneration, Exception exception)
    {
        var completion = router.BeginRecovery(failedGeneration);
        if (completion is null) return;
        _ = Task.Run(() => RecoverAfterOperationFailureAsync(failedGeneration, exception, completion));
    }

    private async Task RecoverAfterOperationFailureAsync(PlannerGeneration failedGeneration, Exception exception, TaskCompletionSource completion)
    {
        try
        {
            await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (rollbackGeneration is null || rollbackModuleId is null)
                    throw new InvalidOperationException("No previous healthy planner generation is available for rollback.", exception);
                await EnsureRunningAndHealthyAsync(rollbackModuleId).ConfigureAwait(false);
                var failedId = failedGeneration.ModuleId;
                _ = router.Restore(rollbackGeneration);
                if (runtime.GetHealth(failedId).State == ModuleLifecycleState.Running)
                    await runtime.MarkDegradedAsync(failedId, "Planner operation threw; route rolled back.").ConfigureAwait(false);
                await runtime.DrainModuleAsync(failedId).ConfigureAwait(false);
                await failedGeneration.WaitForDrainAsync().ConfigureAwait(false);
                await runtime.StopModuleAsync(failedId).ConfigureAwait(false);
                rollbackGeneration = null;
                rollbackModuleId = null;
                Rollbacks.Add(1, new KeyValuePair<string, object?>("failed_module", failedId));
            }
            finally { lifecycleGate.Release(); }
            router.CompleteRecovery(null);
        }
        catch (Exception recoveryFailure)
        {
            router.CompleteRecovery(new InvalidOperationException("Playback planner failed and automatic rollback did not complete.", recoveryFailure));
        }
    }

    private async Task RestoreAfterFailedActivationAsync(PlannerGeneration previousGeneration, string targetModuleId)
    {
        var recovery = router.BeginRecovery(router.ActiveGeneration);
        if (recovery is null) return;
        try
        {
            await EnsureRunningAndHealthyAsync(previousGeneration.ModuleId).ConfigureAwait(false);
            var failed = router.Restore(previousGeneration);
            var targetState = runtime.GetHealth(targetModuleId).State;
            if (targetState == ModuleLifecycleState.Running)
                await runtime.MarkDegradedAsync(targetModuleId, "Planner replacement activation failed; previous route restored.").ConfigureAwait(false);
            if (runtime.GetHealth(targetModuleId).State is ModuleLifecycleState.Running or ModuleLifecycleState.Degraded)
            {
                await runtime.DrainModuleAsync(targetModuleId).ConfigureAwait(false);
                await failed.WaitForDrainAsync().ConfigureAwait(false);
                await runtime.StopModuleAsync(targetModuleId).ConfigureAwait(false);
            }
            router.CompleteRecovery(null);
        }
        catch (Exception rollbackFailure) { router.CompleteRecovery(rollbackFailure); }
    }

    private async Task EnsureRunningAndHealthyAsync(string moduleId)
    {
        var state = runtime.GetHealth(moduleId).State;
        if (state is ModuleLifecycleState.Registered or ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed)
            await runtime.StartModuleAsync(moduleId, CancellationToken.None).ConfigureAwait(false);
        else if (state == ModuleLifecycleState.Draining)
            await runtime.ResumeModuleAsync(moduleId, CancellationToken.None).ConfigureAwait(false);
        if (!await runtime.CheckHealthAsync(moduleId, CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException($"Planner '{moduleId}' failed its rollback health check.");
    }

    public void Dispose()
    {
        router.OperationFailed -= OnOperationFailed;
        lifecycleGate.Dispose();
    }
}
