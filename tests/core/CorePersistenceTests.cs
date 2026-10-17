using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Hispania.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Concurrent;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class CorePersistenceTests
{
    private static readonly ConcurrentDictionary<int, string> CertificateThumbprints = new();
    [Fact]
    public async Task FreshDatabaseIsMigratedAndInitializedBeforeHostStarts()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);

        await app.StartAsync();

        var state = await app.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.NotEqual(Guid.Empty, state.InstallationId);
        Assert.True(File.Exists(Path.Combine(directory.Path, CoreStorageOptions.DatabaseFileName)));
        await using var connection = await OpenAsync(directory.Path);
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal("ok", await ScalarAsync(connection, "PRAGMA quick_check;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM core_schema_migrations WHERE version = 1;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM core_instance WHERE singleton_id = 1;"));
        await app.StopAsync();
    }

    [Fact]
    public async Task ExistingDatabaseRetainsCoreInstallationStateAcrossHostRestart()
    {
        using var directory = new TemporaryDirectory();
        Guid firstId;
        DateTimeOffset firstCreated;
        await using (var first = Build(directory.Path))
        {
            await first.StartAsync();
            var firstState = await first.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
            firstId = firstState.InstallationId;
            firstCreated = firstState.CreatedUtc;
            await first.StopAsync();
        }

        await using var second = Build(directory.Path);
        await second.StartAsync();
        var secondState = await second.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.Equal(firstId, secondState.InstallationId);
        Assert.Equal(firstCreated, secondState.CreatedUtc);
        await second.StopAsync();
    }

    [Fact]
    public async Task StartupWaitsForTransientWriterUsingTheConfiguredBusyTimeout()
    {
        using var directory = new TemporaryDirectory();
        await using (var initial = Build(directory.Path))
        {
            await initial.StartAsync();
            await initial.StopAsync();
        }

        await using var writer = await OpenAsync(directory.Path);
        await using var transaction = writer.BeginTransaction(deferred: false);
        await using (var update = writer.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE core_instance SET created_utc = created_utc WHERE singleton_id = 1;";
            await update.ExecuteNonQueryAsync();
        }

        await using var app = Build(directory.Path);
        var start = app.StartAsync();
        await Task.Delay(200);
        Assert.False(start.IsCompleted);
        await transaction.CommitAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        var state = await app.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.NotEqual(Guid.Empty, state.InstallationId);
        await app.StopAsync();
    }

    [Fact]
    public async Task InitializationIsIdempotentAndConcurrentHostsShareOneInstanceRecord()
    {
        using var directory = new TemporaryDirectory();
        var firstPort = GetAvailableLoopbackPort();
        var secondPort = GetAvailableLoopbackPort();
        while (secondPort == firstPort) secondPort = GetAvailableLoopbackPort();
        await using var first = Build(directory.Path, firstPort);
        await using var second = Build(directory.Path, secondPort);

        await Task.WhenAll(first.StartAsync(), second.StartAsync());

        var firstState = await first.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        var secondState = await second.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.Equal(firstState.InstallationId, secondState.InstallationId);
        await first.StopAsync();
        await second.StopAsync();

        await using var connection = await OpenAsync(directory.Path);
        Assert.Equal("2", await ScalarAsync(connection, "SELECT COUNT(*) FROM core_schema_migrations;"));
        Assert.Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM core_instance;"));
    }

    [Fact]
    public async Task FutureSchemaFailsClosedWithoutDeletingTheDatabase()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, CoreStorageOptions.DatabaseFileName);
        Directory.CreateDirectory(directory.Path);
        await using (var connection = await OpenAsync(directory.Path))
        {
            await ExecuteAsync(connection, "CREATE TABLE core_schema_migrations (version INTEGER NOT NULL PRIMARY KEY, applied_utc TEXT NOT NULL);");
            await ExecuteAsync(connection, "INSERT INTO core_schema_migrations VALUES (99, 'future');");
        }

        var port = GetAvailableLoopbackPort();
        await using var app = Build(directory.Path, port);
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        Assert.True(File.Exists(path));
        Assert.False(await CanConnectAsync(port));
        await using var check = await OpenAsync(directory.Path);
        Assert.Equal("99", await ScalarAsync(check, "SELECT version FROM core_schema_migrations;"));
        Assert.Equal("0", await ScalarAsync(check, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='core_instance';"));
    }

    [Fact]
    public async Task CorruptDatabaseFailsClosedAndPreservesOriginalBytes()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, CoreStorageOptions.DatabaseFileName);
        var original = "not a SQLite database; preserve these bytes"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        var before = SHA256.HashData(original);

        await using var app = Build(directory.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task UnknownTablesWithoutMigrationHistoryAreNotAdoptedOrDeleted()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await using (var connection = await OpenAsync(directory.Path))
            await ExecuteAsync(connection, "CREATE TABLE operator_data (value TEXT NOT NULL);");

        await using var app = Build(directory.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        await using var check = await OpenAsync(directory.Path);
        Assert.Equal("1", await ScalarAsync(check, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='operator_data';"));
        Assert.Equal("0", await ScalarAsync(check, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='core_instance';"));
    }

    [Fact]
    public async Task FailedMigrationRollsBackItsChangesAndPreservesExistingSchema()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await using (var connection = await OpenAsync(directory.Path))
        {
            await ExecuteAsync(connection, "CREATE TABLE core_schema_migrations (version INTEGER NOT NULL PRIMARY KEY CHECK(version > 0), applied_utc TEXT NOT NULL);");
            await ExecuteAsync(connection, "CREATE TABLE core_instance (operator_owned TEXT NOT NULL);");
        }

        await using var app = Build(directory.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        await using var check = await OpenAsync(directory.Path);
        Assert.Equal("0", await ScalarAsync(check, "SELECT COUNT(*) FROM core_schema_migrations;"));
        Assert.Equal("1", await ScalarAsync(check, "SELECT COUNT(*) FROM pragma_table_info('core_instance') WHERE name='operator_owned';"));
        Assert.Equal("0", await ScalarAsync(check, "SELECT COUNT(*) FROM pragma_table_info('core_instance') WHERE name='installation_id';"));
    }

    [Fact]
    public void StorageConfigurationRequiresAbsolutePathOutsidePublishDirectory()
    {
        var relative = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIRECTORY"] = ".\\data" }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreStorageOptions.Read(relative, AppContext.BaseDirectory));

        var publishDirectory = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIRECTORY"] = AppContext.BaseDirectory }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreStorageOptions.Read(publishDirectory, AppContext.BaseDirectory));
    }

    [Fact]
    public async Task HealthDoesNotRevealDatabasePathOrInstallationIdentifier()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.StartAsync();
        var port = GetAvailableLoopbackPortFromServer(app);
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                CertificateThumbprints.TryGetValue(port, out var thumbprint) && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase)
        }) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };
        // CoreHost binds an ephemeral test port through the explicit host override below.
        using var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();
        var state = await app.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.DoesNotContain(directory.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(state.InstallationId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        await app.StopAsync();
    }

    private static WebApplication Build(string dataDirectory, int? selectedPort = null)
    {
        var port = selectedPort ?? GetAvailableLoopbackPort();
        Directory.CreateDirectory(dataDirectory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = System.IO.Path.Combine(dataDirectory, $"test-{Guid.NewGuid():N}.pfx");
        var certificatePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(certificatePath, generated.Export(X509ContentType.Pfx, certificatePassword));
        CertificateThumbprints[port] = generated.Thumbprint;
        return CoreHost.Build(new Dictionary<string, string?>
        {
            ["LISTEN_ADDRESS"] = "127.0.0.1",
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SHUTDOWN_TIMEOUT_SECONDS"] = "5",
            ["DATA_DIRECTORY"] = dataDirectory,
            ["HTTPS_CERTIFICATE_PATH"] = certificatePath,
            ["HTTPS_CERTIFICATE_PASSWORD"] = certificatePassword
        });
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or TimeoutException)
        {
            return false;
        }
    }

    private static int GetAvailableLoopbackPortFromServer(WebApplication app)
    {
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        var address = new Uri(addresses.Addresses.Single());
        return address.Port;
    }

    private static async Task<SqliteConnection> OpenAsync(string dataDirectory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, CoreStorageOptions.DatabaseFileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-core-sqlite-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
