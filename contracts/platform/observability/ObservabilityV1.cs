using System.Collections.Frozen;
using System.Diagnostics;

namespace Hispania.Platform.Contracts.Observability;

public sealed record CorrelationContextV1(string CorrelationId, string? CausationId = null, string? OperationId = null, string? TraceParent = null, string? TraceState = null);

/// <summary>Logical execution scope; always restore the previous scope after asynchronous work.</summary>
public static class PlatformCorrelationV1
{
    public const string HeaderName = "X-Correlation-ID";
    private static readonly AsyncLocal<CorrelationContextV1?> Context = new();
    public static CorrelationContextV1? Current => Context.Value;
    public static bool IsValidId(string? value) => value is { Length: >= 1 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    public static IDisposable Push(CorrelationContextV1 context)
    {
        if (!IsValidId(context.CorrelationId) || context.CausationId is not null && !IsValidId(context.CausationId) || context.OperationId is not null && !IsValidId(context.OperationId))
            throw new ArgumentException("Invalid correlation metadata.", nameof(context));
        var previous = Context.Value;
        Context.Value = context;
        return new Scope(previous);
    }
    private sealed class Scope(CorrelationContextV1? previous) : IDisposable
    {
        public void Dispose() => Context.Value = previous;
    }
}

public static class PlatformTracingV1
{
    public static ActivitySource Source { get; } = new("Hispania.Platform", "1.0.0");
    public static Activity? Start(string operation, ActivityKind kind = ActivityKind.Internal, string? traceParent = null, string? traceState = null)
    {
        var previous = Activity.Current;
        try
        {
            traceParent ??= PlatformCorrelationV1.Current?.TraceParent;
            traceState ??= PlatformCorrelationV1.Current?.TraceState;
            var activity = ActivityContext.TryParse(traceParent, traceState, true, out var parent)
                ? Source.StartActivity(operation, kind, parent) : Source.StartActivity(operation, kind);
            activity?.SetTag("correlation.id", PlatformCorrelationV1.Current?.CorrelationId);
            activity?.SetTag("causation.id", PlatformCorrelationV1.Current?.CausationId);
            return activity;
        }
        catch (Exception)
        {
            if (Activity.Current is { } failed && failed != previous) { try { failed.Dispose(); } catch (Exception) { } }
            Activity.Current = previous;
            return null;
        }
    }
    public static IDisposable? StartScope(string operation, ActivityKind kind = ActivityKind.Internal, string? traceParent = null, string? traceState = null)
    {
        var previous = Activity.Current;
        var activity = Start(operation, kind, traceParent, traceState);
        return activity is null ? null : new BestEffortActivityScope(activity, previous);
    }
    private sealed class BestEffortActivityScope(Activity activity, Activity? previous) : IDisposable
    {
        public void Dispose()
        {
            try { activity.Dispose(); } catch (Exception) { }
            finally { if (Activity.Current == activity) Activity.Current = previous; }
        }
    }
}

public enum PlatformHealthStatusV1 { Healthy, Degraded, Unhealthy }
public sealed record PlatformHealthCheckResultV1(string Component, PlatformHealthStatusV1 Status, string Code);
public sealed record PlatformHealthSnapshotV1(PlatformHealthStatusV1 Status, DateTimeOffset CheckedAt, IReadOnlyList<PlatformHealthCheckResultV1> Checks);
public interface IPlatformHealthCheckV1
{
    string Component { get; }
    ValueTask<PlatformHealthCheckResultV1> CheckAsync(CancellationToken cancellationToken = default);
}
public interface IPlatformHealthV1
{
    ValueTask<PlatformHealthSnapshotV1> CheckAsync(CancellationToken cancellationToken = default);
}
public sealed record PlatformMetricSnapshotV1(string Name, string Kind, double Value, long Count = 0);
public interface IPlatformMetricsV1
{
    void Increment(string name, long amount = 1);
    void Gauge(string name, long value);
    void Observe(string name, double milliseconds);
    IReadOnlyList<PlatformMetricSnapshotV1> Snapshot();
}

/// <summary>Fixed vocabulary bounds memory and prevents accidental identifier labels. No labels in V1.</summary>
public static class PlatformMetricNamesV1
{
    public static IReadOnlySet<string> Counters { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "hispania.identity.login.success", "hispania.identity.login.failure",
        "hispania.security.authentication.rate_limited", "hispania.security.authorization.denied", "hispania.security.audit.denial_suppressed",
        "hispania.events.published", "hispania.events.delivered", "hispania.events.retrying", "hispania.events.failed", "hispania.events.dead_lettered",
        "hispania.jobs.created", "hispania.jobs.retried", "hispania.jobs.execution.local", "hispania.jobs.execution.remote", "hispania.jobs.remote.unavailable", "hispania.jobs.remote.retried", "hispania.jobs.remote.fenced_results",
        "hispania.storage.reads", "hispania.storage.writes", "hispania.storage.deletes", "hispania.storage.failures", "hispania.storage.read.failure", "hispania.storage.write.failure",
        "hispania.workers.executions", "hispania.workers.failures"
    }.ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> Gauges { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "hispania.identity.sessions.active", "hispania.events.pending", "hispania.events.processing", "hispania.events.backlog.retrying", "hispania.events.backlog.dead_lettered",
        "hispania.jobs.pending", "hispania.jobs.running", "hispania.jobs.succeeded", "hispania.jobs.failed", "hispania.jobs.cancelled", "hispania.jobs.dead_lettered", "hispania.workers.connected", "hispania.workers.capabilities"
    }.ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> Histograms { get; } = new HashSet<string>(StringComparer.Ordinal) { "hispania.http.duration", "hispania.storage.duration", "hispania.jobs.duration" }.ToFrozenSet(StringComparer.Ordinal);
}

public static class PlatformMetricsExtensionsV1
{
    public static void BestEffort(this IPlatformMetricsV1? metrics, Action<IPlatformMetricsV1> record)
    { if (metrics is null) return; try { record(metrics); } catch (Exception) { } }
}
