using Hispania.Media.Domain.Identity;
using Hispania.Playback.Contracts;

namespace Hispania.Playback.Application;

/// <summary>Stable per-operation planner reference with generation-based draining.</summary>
public sealed class HotSwappablePlaybackPlanner : IPlaybackPlanner
{
    private readonly object gate = new();
    private PlannerGeneration active;
    private Task? recoveryTask;
    private TaskCompletionSource? recoveryCompletion;

    public HotSwappablePlaybackPlanner(string initialModuleId, IPlaybackPlanner initial)
    {
        active = new PlannerGeneration(initialModuleId, initial);
    }

    public event Action<PlannerGeneration, Exception>? OperationFailed;
    public string ActiveModuleId { get { lock (gate) return active.ModuleId; } }
    public PlannerGeneration ActiveGeneration { get { lock (gate) return active; } }

    public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId)
    {
        using var lease = Acquire();
        try { return lease.Generation.Implementation.Plan(mediaEntityId); }
        catch (Exception exception)
        {
            OperationFailed?.Invoke(lease.Generation, exception);
            throw;
        }
    }

    public PlannerGeneration Replace(string moduleId, IPlaybackPlanner implementation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(implementation);
        lock (gate)
        {
            if (recoveryTask is not null) throw new InvalidOperationException("Playback planner rollback is in progress.");
            if (active.ModuleId == moduleId) throw new InvalidOperationException("The requested planner is already active.");
            var previous = active;
            active = new PlannerGeneration(moduleId, implementation);
            previous.StopAccepting();
            return previous;
        }
    }

    public PlannerGeneration Restore(PlannerGeneration previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        lock (gate)
        {
            var failed = active;
            failed.StopAccepting();
            previous.ResumeAccepting();
            active = previous;
            return failed;
        }
    }

    public TaskCompletionSource? BeginRecovery(PlannerGeneration failed)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, failed) || recoveryCompletion is not null) return null;
            recoveryCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recoveryTask = recoveryCompletion.Task;
            return recoveryCompletion;
        }
    }

    public void CompleteRecovery(Exception? failure)
    {
        TaskCompletionSource? completion;
        lock (gate)
        {
            completion = recoveryCompletion;
            if (failure is null)
            {
                recoveryCompletion = null;
                recoveryTask = null;
            }
        }
        if (failure is null) completion?.TrySetResult();
        else completion?.TrySetException(failure);
    }

    private PlannerOperationLease Acquire()
    {
        while (true)
        {
            PlannerGeneration candidate;
            Task? recovery;
            lock (gate)
            {
                recovery = recoveryTask;
                candidate = active;
                if (recovery is null && candidate.TryAcquire()) return new PlannerOperationLease(candidate);
            }
            if (recovery is not null) recovery.GetAwaiter().GetResult();
            else Thread.Yield();
        }
    }
}

public sealed class PlannerGeneration(string moduleId, IPlaybackPlanner implementation)
{
    private readonly object gate = new();
    private TaskCompletionSource drainCompletion = CompletedSource();
    private int operationCount;
    private bool accepting = true;

    public string ModuleId { get; } = moduleId;
    public IPlaybackPlanner Implementation { get; } = implementation;

    internal bool TryAcquire()
    {
        lock (gate)
        {
            if (!accepting) return false;
            if (operationCount++ == 0) drainCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    internal void Release()
    {
        lock (gate)
        {
            operationCount--;
            if (!accepting && operationCount == 0) drainCompletion.TrySetResult();
        }
    }

    internal void StopAccepting()
    {
        lock (gate)
        {
            accepting = false;
            if (operationCount == 0) drainCompletion.TrySetResult();
        }
    }

    internal void ResumeAccepting()
    {
        lock (gate)
        {
            accepting = true;
            if (operationCount == 0) drainCompletion = CompletedSource();
        }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) return drainCompletion.Task.WaitAsync(cancellationToken);
    }

    public int ActiveOperations { get { lock (gate) return operationCount; } }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

public sealed class PlannerOperationLease(PlannerGeneration generation) : IDisposable
{
    private PlannerGeneration? generation = generation;
    public PlannerGeneration Generation => generation ?? throw new ObjectDisposedException(nameof(PlannerOperationLease));
    public void Dispose() => Interlocked.Exchange(ref generation, null)?.Release();
}
