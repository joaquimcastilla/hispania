using System.Diagnostics.Metrics;
using Hispania.Platform.Contracts.Observability;
using Microsoft.Extensions.Logging;

namespace Hispania.Platform.Observability.Application;

public sealed class PlatformMetricsV1 : IPlatformMetricsV1, IDisposable
{
    private readonly Meter meter = new("Hispania.Platform", "1.0.0");
    private readonly Dictionary<string, MetricCell> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Counter<long>> counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Histogram<double>> histograms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetricCell> timings = new(StringComparer.Ordinal);
    public PlatformMetricsV1()
    {
        foreach (var name in PlatformMetricNamesV1.Counters) { values[name] = new(); counters[name] = meter.CreateCounter<long>(name); }
        foreach (var name in PlatformMetricNamesV1.Gauges) { values[name] = new(); meter.CreateObservableGauge(name, () => Read(name)); }
        foreach (var name in PlatformMetricNamesV1.Histograms) { timings[name] = new(); histograms[name] = meter.CreateHistogram<double>(name, "ms"); }
    }
    private long Read(string name) => Interlocked.Read(ref values[name].Value);
    public void Increment(string name, long amount = 1)
    {
        if (!counters.TryGetValue(name, out var counter) || amount < 0) throw new ArgumentException("Unknown counter or negative increment.", nameof(name));
        var cell = values[name];
        lock (cell) cell.Value = checked(cell.Value + amount);
        try { counter.Add(amount); } catch (Exception) { /* Exporters are best effort. */ }
    }
    public void Gauge(string name, long value)
    {
        if (!PlatformMetricNamesV1.Gauges.Contains(name) || value < 0) throw new ArgumentException("Unknown gauge or negative value.", nameof(name));
        Interlocked.Exchange(ref values[name].Value, value);
    }
    public void Observe(string name, double milliseconds)
    {
        if (!histograms.TryGetValue(name, out var histogram) || !double.IsFinite(milliseconds) || milliseconds < 0) throw new ArgumentException("Invalid timing.", nameof(name));
        var cell = timings[name];
        lock (cell)
        {
            var total = cell.Total + milliseconds;
            if (!double.IsFinite(total)) throw new OverflowException("Timing aggregate exceeds the supported range.");
            var count = checked(cell.Count + 1);
            cell.Total = total; cell.Count = count;
        }
        try { histogram.Record(milliseconds); } catch (Exception) { }
    }
    public IReadOnlyList<PlatformMetricSnapshotV1> Snapshot()
    {
        return values.Select(pair => new PlatformMetricSnapshotV1(pair.Key, counters.ContainsKey(pair.Key) ? "Counter" : "Gauge", Interlocked.Read(ref pair.Value.Value)))
            .Concat(timings.Select(pair =>
            {
                var cell = pair.Value;
                lock (cell) return new PlatformMetricSnapshotV1(pair.Key, "HistogramMeanMs", cell.Count == 0 ? 0 : cell.Total / cell.Count, cell.Count);
            })).OrderBy(row => row.Name, StringComparer.Ordinal).ToArray();
    }
    private sealed class MetricCell
    {
        public long Value;
        public double Total;
        public long Count;
    }
    public void Dispose() => meter.Dispose();
}

public sealed class PlatformHealthV1(IEnumerable<IPlatformHealthCheckV1> checks) : IPlatformHealthV1
{
    public async ValueTask<PlatformHealthSnapshotV1> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PlatformHealthCheckResultV1>();
        foreach (var check in checks)
        {
            try { results.Add(await check.CheckAsync(cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { results.Add(new(check.Component, PlatformHealthStatusV1.Unhealthy, "check_failed")); }
        }
        return new(results.Count == 0 ? PlatformHealthStatusV1.Unhealthy : results.Max(result => result.Status), DateTimeOffset.UtcNow, results);
    }
}

public sealed class DelegatePlatformHealthCheckV1(string component, Func<CancellationToken, ValueTask<PlatformHealthCheckResultV1>> check) : IPlatformHealthCheckV1
{
    public string Component => component;
    public ValueTask<PlatformHealthCheckResultV1> CheckAsync(CancellationToken cancellationToken = default) => check(cancellationToken);
}

public static class PlatformLogConventionsV1
{
    public static IDisposable? BeginOperation(ILogger logger, string component, string operation) => logger.BeginScope(new Dictionary<string, object?>
    {
        ["CorrelationId"] = PlatformCorrelationV1.Current?.CorrelationId, ["CausationId"] = PlatformCorrelationV1.Current?.CausationId,
        ["Component"] = component, ["Operation"] = operation
    });
    public static readonly EventId JobStarted = new(13801, "JobStarted");
    public static readonly EventId JobCompleted = new(13802, "JobCompleted");
    public static readonly EventId JobFailed = new(13803, "JobFailed");
    public static readonly EventId EventDeadLettered = new(13804, "EventDeadLettered");
    public static readonly EventId WorkerConnected = new(13805, "WorkerConnected");
    public static readonly EventId StorageFailure = new(13806, "StorageFailure");
    public static readonly EventId LoginFailed = new(13807, "LoginFailed");
}
