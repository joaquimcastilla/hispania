using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Hispania.Persistence.Schema;

// Shared SQLite mechanics only; each owner supplies its own ordered plan and schema.
internal sealed record SqliteSchemaMigration(int Version, string Id, Action<SqliteConnection, SqliteTransaction> Apply);
internal sealed record SqliteSchemaState(int CurrentSchemaVersion, int MinimumSupportedSchema, IReadOnlyList<string> AppliedMigrations);
internal static class SqliteSchemaUpgrade
{
    private static readonly DiagnosticListener Diagnostics = new("Hispania.SchemaMigration");
    private static readonly Meter Meter = new("Hispania.SchemaMigration", "1.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("hispania.schema.migration.duration", "ms");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("hispania.schema.migration.failures");
    internal static void Open(SqliteConnection connection, string owner)
    {
        try { connection.Open(); }
        catch (SqliteException ex) { throw new InvalidOperationException($"Store {owner}: database could not be opened; preserve it for explicit recovery. No reset was performed.", ex); }
    }
    internal static void Initialize(SqliteConnection connection, string owner, string sql, params (string Table, string Column, string Declaration)[] additions)
    {
        Apply(connection, owner, [new(1, "0001_baseline07_adoption", (db, tx) =>
        {
            Execute(db, tx, sql.Replace("PRAGMA journal_mode=WAL;", "", StringComparison.Ordinal));
            foreach (var (table, column, declaration) in additions)
            {
                using var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = $"PRAGMA table_info({table})";
                using var r = q.ExecuteReader(); var found = false;
                while (r.Read()) found |= r.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase);
                r.Close();
                if (!found) Execute(db, tx, $"ALTER TABLE {table} ADD COLUMN {column} {declaration}");
            }
            ValidateShape(db, tx, owner, sql, additions);
        })]);
        ValidateShape(connection, null, owner, sql, additions);
        if (sql.Contains("PRAGMA journal_mode=WAL;", StringComparison.Ordinal))
        { using var q = connection.CreateCommand(); q.CommandText = "PRAGMA journal_mode=WAL"; q.ExecuteNonQuery(); }

    }
    private static void ValidateShape(SqliteConnection connection, SqliteTransaction? transaction, string owner, string sql, (string Table, string Column, string Declaration)[] additions)
    {
        foreach (var (table, column, _) in additions)
        { using var q = connection.CreateCommand(); q.Transaction=transaction; q.CommandText=$"SELECT {column} FROM {table} LIMIT 0"; using var r=q.ExecuteReader(); }
        // Check the selected owner shape even when an already-applied ledger is present.
        foreach (System.Text.RegularExpressions.Match table in System.Text.RegularExpressions.Regex.Matches(sql, @"CREATE TABLE IF NOT EXISTS (\w+)\s*\(([^;]+)\);"))
        {
            var expected = table.Groups[2].Value.Split(',').Select(part => part.Trim().Split(' ')[0])
                .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^\w+$") && name is not "PRIMARY" and not "FOREIGN" and not "UNIQUE" and not "CHECK").ToArray();
            using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = $"SELECT {string.Join(',', expected)} FROM {table.Groups[1].Value} LIMIT 0";
            try { using var reader = query.ExecuteReader(); }
            catch (SqliteException ex) { throw new InvalidOperationException($"Store {owner}: schema shape is incompatible; explicit recovery is required.", ex); }
        }
    }
    internal static SqliteSchemaState Read(SqliteConnection db, string owner, IReadOnlyList<SqliteSchemaMigration> plan, SqliteTransaction? tx = null)
    {
        using var q = db.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='hispania_schema_history'";
        if (Convert.ToInt64(q.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) return new(0, 0, []);
        var companion = owner == "Platform.Identity" ? "Platform.Authorization" : owner == "Platform.Authorization" ? "Platform.Identity" : owner;
        q.CommandText = "SELECT owner,version,migration_id FROM hispania_schema_history WHERE owner IN ($owner,$companion) ORDER BY owner,version";
        q.Parameters.AddWithValue("$companion", companion);
        q.Parameters.AddWithValue("$owner", owner);
        using var r = q.ExecuteReader(); var ids = new List<string>(); var companionCount = 0;
        while (r.Read())
        {
            var recordedOwner = r.GetString(0); var version = r.GetInt32(1); var id = r.GetString(2);
            // Identity/RBAC deliberately share one physical file: validate both readers before any write.
            if (recordedOwner != owner)
            {
                if (version != ++companionCount || version != 1 || id != "0001_baseline07_adoption")
                    throw new InvalidOperationException($"Store {owner}: co-owned security schema is future or invalid; use a compatible executable.");
                continue;
            }
            if (version > plan.Count) throw new InvalidOperationException($"Store {owner}: future schema; use a compatible newer executable. Downgrade is unsupported.");
            if (version != ids.Count + 1 || version < 1 || id != plan[version - 1].Id)
                throw new InvalidOperationException($"Store {owner}: invalid migration history; explicit recovery is required.");
            ids.Add(id);
        }
        return new(ids.Count, 0, ids);
    }
    internal static void Apply(SqliteConnection db, string owner, IReadOnlyList<SqliteSchemaMigration> plan)
    {
        if (plan.Select((m, i) => m.Version != i + 1).Any(x => x) || plan.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != plan.Count)
            throw new ArgumentException("Migration plan must have unique IDs and contiguous ordered versions.", nameof(plan));
        var clock = Stopwatch.StartNew(); var from = 0; var migration = "validation";
        try
        {
            // Reject a future/corrupt ledger before acquiring a writer lock or changing journal mode.
            from = Read(db, owner, plan).CurrentSchemaVersion;
            using var tx = db.BeginTransaction(deferred: false);
            from = Read(db, owner, plan, tx).CurrentSchemaVersion; // Recheck after SQLite writer exclusion.
            if (from == plan.Count) { tx.Rollback(); return; }
            Execute(db, tx, "CREATE TABLE IF NOT EXISTS hispania_schema_history(owner TEXT NOT NULL,version INTEGER NOT NULL CHECK(version>0),migration_id TEXT NOT NULL,PRIMARY KEY(owner,version),UNIQUE(owner,migration_id));");
            foreach (var step in plan.Skip(from))
            {
                migration = step.Id;
                step.Apply(db, tx);
                using var q = db.CreateCommand(); q.Transaction = tx;
                q.CommandText = "INSERT INTO hispania_schema_history(owner,version,migration_id) VALUES($owner,$version,$id)";
                q.Parameters.AddWithValue("$owner", owner); q.Parameters.AddWithValue("$version", step.Version); q.Parameters.AddWithValue("$id", step.Id); q.ExecuteNonQuery();
            }
            tx.Commit();
            foreach (var step in plan.Skip(from))
            {
                Trace.TraceInformation("Schema migration store={0} from={1} to={2} migration={3} result=success", owner, step.Version-1, step.Version, step.Id);
                Diagnostics.Write("MigrationSucceeded", new { Store = owner, From = step.Version-1, To = step.Version, MigrationId = step.Id });
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Trace.TraceWarning("Schema migration store={0} from={1} to={2} migration={3} result=failure", owner, from, plan.Count, migration);
            Failures.Add(1, new KeyValuePair<string, object?>("store", owner));
            Diagnostics.Write("MigrationFailed", new { Store = owner, From = from, To = plan.Count, MigrationId = migration });
            throw new InvalidOperationException($"Store {owner}: schema upgrade refused or failed; preserve this database and use a compatible executable or explicit recovery. No reset was performed.", ex);
        }
        finally { Duration.Record(clock.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("store", owner)); }
    }
    internal static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    { using var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = sql; q.ExecuteNonQuery(); }
}
