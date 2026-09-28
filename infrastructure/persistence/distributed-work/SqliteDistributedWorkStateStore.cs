using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Microsoft.Data.Sqlite;

namespace Hispania.DistributedWork.Persistence.Sqlite;

/// <summary>Transactional durable state for one coordinator process. The lock file prevents split-brain coordinators.</summary>
public sealed class SqliteDistributedWorkStateStore : IDistributedWorkStateStoreV1, IDisposable
{
    private readonly string connectionString;
    private readonly FileStream coordinatorLock;
    private readonly object gate = new();

    public SqliteDistributedWorkStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        coordinatorLock = new FileStream(fullPath + ".coordinator.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS distributed_coordinator_state (id INTEGER PRIMARY KEY CHECK(id = 1), payload TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public DistributedCoordinatorStateV1? Load()
    {
        lock (gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM distributed_coordinator_state WHERE id = 1;";
            var payload = command.ExecuteScalar() as string;
            return payload is null ? null : JsonSerializer.Deserialize<DistributedCoordinatorStateV1>(payload);
        }
    }

    public void Save(DistributedCoordinatorStateV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false); using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO distributed_coordinator_state(id, payload) VALUES(1, $payload) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;";
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state));
            command.ExecuteNonQuery(); transaction.Commit();
        }
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public void Dispose() => coordinatorLock.Dispose();
}
