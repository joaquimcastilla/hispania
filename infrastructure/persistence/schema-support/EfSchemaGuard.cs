using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace Hispania.Persistence.Schema;
internal static class EfSchemaGuard
{
    private static readonly Meter Meter = new("Hispania.SchemaMigration.EF", "1.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("hispania.schema.migration.duration", "ms");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("hispania.schema.migration.failures");
    internal static async Task UpgradeAsync(DbContext context, string owner, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? aliases = null, Func<Task>? normalize = null)
    {
        var clock = Stopwatch.StartNew(); var from = 0; var to = 0;
        try
        {
            await ValidateAsync(context, owner, cancellationToken, aliases);
            from = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).Count();
            var known = context.Database.GetMigrations().ToArray(); to = known.Length;
            if (normalize is not null) await normalize();
            await context.Database.MigrateAsync(cancellationToken);
            foreach (var (id, index) in known.Select((id, index) => (id, index)).Skip(from))
                Trace.TraceInformation("Schema migration store={0} from={1} to={2} migration={3} result=success", owner, index, index + 1, id);
        }
        catch
        {
            Trace.TraceWarning("Schema migration store={0} from={1} to={2} migration=validation-or-EF-upgrade result=failure", owner, from, to);
            Failures.Add(1, new KeyValuePair<string, object?>("store", owner));
            throw;
        }
        finally { Duration.Record(clock.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("store", owner)); }
    }
    internal static async Task ValidateAsync(DbContext context, string owner, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var known = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var normalized = applied.Select(id => aliases is not null && aliases.TryGetValue(id, out var mapped) ? mapped : id).ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length || normalized.Any(id => !known.Contains(id, StringComparer.Ordinal)) ||
            !known.Take(normalized.Length).SequenceEqual(normalized.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException($"Store {owner}: future, unknown or discontinuous EF migration history; preserve the database and use a compatible executable. Downgrade is unsupported.");
    }
}
