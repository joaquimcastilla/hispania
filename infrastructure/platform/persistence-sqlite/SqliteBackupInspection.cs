using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

// Provider-level read-only integrity and shared adoption-ledger inspection.
// Domain EF histories are supplied by their owner, never by Platform.
public static class SqliteBackupInspection
{
    public static string Inspect(string path, Func<SqliteConnection, string>? inspectEfHistory = null)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using (var reader = command.ExecuteReader()) { if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read()) throw new InvalidDataException("SQLite integrity failed."); }
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader()) if (reader.Read()) throw new InvalidDataException("SQLite foreign-key integrity failed.");
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('hispania_schema_history','__EFMigrationsHistory') ORDER BY name";
        var tables = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        var records = new List<string>();
        foreach (var table in tables)
        {
            if (table == "__EFMigrationsHistory")
            {
                if (inspectEfHistory is null) throw new InvalidDataException("Unexpected EF schema history in a non-Media store.");
                records.Add(inspectEfHistory(db)); continue;
            }
            command.CommandText = "SELECT owner,version,migration_id FROM hispania_schema_history ORDER BY owner,version";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (table == "hispania_schema_history" && (reader.GetInt32(1) != 1 || reader.GetString(2) != "0001_baseline07_adoption")) throw new InvalidDataException("Unsupported SQLite schema.");
                records.Add(string.Join(':', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
            }
        }
        return records.Count == 0 ? "legacy:0" : string.Join('|', records);
    }
}
