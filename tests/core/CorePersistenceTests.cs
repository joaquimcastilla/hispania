using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Hispania.Core.Modules;
using Hispania.Core.Persistence;
using Hispania.Runtime;
using Hispania.ModuleSystem.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        Assert.Equal("3", await ScalarAsync(connection, "SELECT COUNT(*) FROM core_schema_migrations;"));
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
    public void StorageConfigurationAllowsExternalRootAndSimilarPrefixDirectory()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var publish = Path.Combine(directory.Path, "core-publish");
        Directory.CreateDirectory(publish);
        var data = Path.Combine(directory.Path, "core-publish-data");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIRECTORY"] = data }).Build();
        var options = CoreStorageOptions.Read(configuration, publish);
        Assert.Equal(Path.GetFullPath(data), options.DataDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(data), CoreStorageOptions.DatabaseFileName), options.DatabasePath);
    }

    [Fact]
    public void StorageConfigurationRejectsPublishOverlapWithWindowsCaseRules()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var publish = Path.Combine(directory.Path, "CorePublish");
        Directory.CreateDirectory(publish);
        var inside = Path.Combine(directory.Path, "corepublish", "data");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIRECTORY"] = inside }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreStorageOptions.Read(configuration, publish));
    }

    [Fact]
    public void StoragePathGuardFailsClosedWhenAnAncestorIsNotADirectory()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var file = Path.Combine(directory.Path, "ancestor-file");
        File.WriteAllText(file, "owned test data");
        Assert.Throws<InvalidDataException>(() => StoragePathGuard.EnsureNoReparsePoints(Path.Combine(file, "child")));
    }

    [Fact]
    public void StoragePathGuardRejectsPathWithSimilarRootPrefix()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "store");
        var sibling = Path.Combine(directory.Path, "store-foreign");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        Assert.Throws<InvalidDataException>(() => StoragePathGuard.RequireWithin(root, sibling, "path escaped authorized root"));
    }

    [Fact(Skip = "Owner diagnostic, PowerShell 7.6.7 (2026-10-18): Directory.CreateSymbolicLink failed because the token lacks the required symbolic-link privilege. Exact exception/HResult was not supplied. Junctions are covered by separate tests.")]
    public void StoragePathGuardDetectsLinksWithoutTraversingOrDeletingTheirTargets()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var store = Path.Combine(directory.Path, "store");
        var outside = Path.Combine(directory.Path, "outside");
        var publish = Path.Combine(directory.Path, "publish");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(publish);
        var unrelated = Path.Combine(outside, "operator-owned.txt");
        File.WriteAllText(unrelated, "preserve");
        var link = Path.Combine(store, "external-link");
        Directory.CreateSymbolicLink(link, outside);
        var publishLink = Path.Combine(store, "publish-link");
        Directory.CreateSymbolicLink(publishLink, publish);

        Assert.Throws<InvalidDataException>(() => StoragePathGuard.EnsureTreeHasNoReparsePoints(store));
        Assert.Throws<InvalidDataException>(() => StoragePathGuard.RequireWithin(store, Path.Combine(link, "staging"), "staging escaped"));
        Assert.Throws<InvalidDataException>(() => StoragePathGuard.RequireOutside(publishLink, publish, "data overlaps publish"));
        var linkedData = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIRECTORY"] = publishLink }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreStorageOptions.Read(linkedData, publish));
        Assert.True(File.Exists(unrelated));
        Assert.True(Directory.Exists(link));
        Assert.True(Directory.Exists(publishLink));
    }

    [Fact]
    public void StoragePathGuardRejectsDataDirectoryJunctionBeforeBootstrapWrites()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var target = Path.Combine(directory.Path, "external-data");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "operator-owned.txt");
        File.WriteAllText(sentinel, "keep this external file");
        var junction = Path.Combine(directory.Path, "configured-data");
        directory.CreateJunction(junction, target);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATA_DIRECTORY"] = junction
            }).Build();

            Assert.Throws<CoreConfigurationException>(() => CoreStorageOptions.Read(configuration, Path.Combine(directory.Path, "core-publish")));
            Assert.False(File.Exists(Path.Combine(target, CoreStorageOptions.DatabaseFileName)));
            Assert.Equal("keep this external file", File.ReadAllText(sentinel));
        }
        finally { directory.RemoveJunction(junction); }
    }

    [Fact]
    public async Task CoreModuleManagerRejectsJunctionAtStagingBeforeReadingOrWriting()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var data = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(data);
        var external = Path.Combine(directory.Path, "external-staging");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "operator-owned.txt");
        File.WriteAllText(sentinel, "keep this external file");
        var junction = Path.Combine(data, "module-staging");
        directory.CreateJunction(junction, external);
        using var catalog = CreateStorageTestCatalog(Path.Combine(data, "modules"));
        var manager = new CoreModuleManager(catalog, data);
        using var package = new MemoryStream([1, 2, 3]);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(package, CancellationToken.None));
            Assert.Equal(0, package.Position);
            Assert.Equal("operator-owned.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(external))));
            Assert.Equal("keep this external file", File.ReadAllText(sentinel));
        }
        finally { directory.RemoveJunction(junction); }
    }

    [Fact]
    public async Task ModuleStoreJunctionIsRejectedBeforeDiscoveryOrInstallationWrites()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var external = Path.Combine(directory.Path, "external-module-store");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "operator-owned.txt");
        File.WriteAllText(sentinel, "keep this external file");
        var junction = Path.Combine(directory.Path, "modules");
        directory.CreateJunction(junction, external);
        using var catalog = CreateStorageTestCatalog(junction);
        using var package = new MemoryStream();
        try
        {
            Assert.Throws<InvalidDataException>(() => catalog.Discover());
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InstallAsync(package, CancellationToken.None));
            Assert.Equal(0, package.Position);
            Assert.Equal("operator-owned.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(external))));
            Assert.Equal("keep this external file", File.ReadAllText(sentinel));
        }
        finally { directory.RemoveJunction(junction); }
    }

    [Fact]
    public void DataDirectoryJunctionRedirectingToCorePublishIsRejectedWithoutChangingPublishFiles()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var publish = Path.Combine(System.IO.Path.GetTempPath(), "hispania-slice7-accepted-20261018-publish");
        Assert.True(Directory.Exists(publish));
        publish = Path.GetFullPath(publish);
        var protectedFile = Path.Combine(publish, "Hispania.Core.dll");
        Assert.True(File.Exists(protectedFile));
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(protectedFile)));
        var junction = Path.Combine(directory.Path, "redirected-data");
        directory.CreateJunction(junction, publish);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATA_DIRECTORY"] = junction
            }).Build();

            Assert.Throws<CoreConfigurationException>(() => CoreStorageOptions.Read(configuration, publish));
            Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(protectedFile))));
        }
        finally { directory.RemoveJunction(junction); }
    }

    [Fact]
    public async Task RecursivePackageRemovalRejectsJunctionAndPreservesBothTrees()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var store = Path.Combine(directory.Path, "modules");
        var package = Path.Combine(store, "fixture.module", "1.0.0");
        var nested = Path.Combine(package, "payload");
        var external = Path.Combine(directory.Path, "external-package-data");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(external);
        var packageSentinel = Path.Combine(package, "package-owned.txt");
        var externalSentinel = Path.Combine(external, "operator-owned.txt");
        File.WriteAllText(packageSentinel, "package remains installed");
        File.WriteAllText(externalSentinel, "external data remains intact");
        var junction = Path.Combine(nested, "external-link");
        directory.CreateJunction(junction, external);
        using var catalog = CreateStorageTestCatalog(store, ["fixture.module"]);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.UninstallAsync("fixture.module", "1.0.0"));
            Assert.True(File.Exists(packageSentinel));
            Assert.Equal("package remains installed", File.ReadAllText(packageSentinel));
            Assert.Equal("external data remains intact", File.ReadAllText(externalSentinel));
            Assert.True(Directory.Exists(junction));
        }
        finally { directory.RemoveJunction(junction); }
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

    private static ModuleExtensionCatalog CreateStorageTestCatalog(string root, IEnumerable<string>? allowedIds = null)
    {
        var registry = new ModuleRegistry();
        return new ModuleExtensionCatalog(root, new Version(1, 0, 0), new ModuleRuntime(registry), registry,
            new Dictionary<string, Func<IModule>>(), allowedIds);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-core-sqlite-tests", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, (string Target, Dictionary<string, string> Files)> junctions = new(StringComparer.OrdinalIgnoreCase);

        public void CreateJunction(string linkPath, string targetPath)
        {
            var link = System.IO.Path.GetFullPath(linkPath);
            var target = System.IO.Path.GetFullPath(targetPath);
            var relative = System.IO.Path.GetRelativePath(Path, link);
            if (System.IO.Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Test junction must be inside its owned temporary directory.");
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Test junction target must already exist.");
            var command = $"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path {QuotePowerShell(link)} -Target {QuotePowerShell(target)} | Out-Null";
            InvokePowerShell(command);
            junctions.Add(link, (target, SnapshotFiles(target)));
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException("PowerShell did not create a reparse-point junction.");
        }

        public void RemoveJunction(string linkPath)
        {
            var link = System.IO.Path.GetFullPath(linkPath);
            if (!junctions.TryGetValue(link, out var tracked)) return;
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException("Refusing to remove a test path that is not a reparse point.");
            InvokePowerShell($"$ErrorActionPreference='Stop'; Remove-Item -LiteralPath {QuotePowerShell(link)} -Force");
            if (Directory.Exists(link) || File.Exists(link)) throw new IOException("PowerShell did not remove the test junction itself.");
            if (!Directory.Exists(tracked.Target) || !SnapshotsEqual(tracked.Files, SnapshotFiles(tracked.Target)))
                throw new IOException("Removing the test junction changed files in its external target.");
            junctions.Remove(link);
        }

        private static Dictionary<string, string> SnapshotFiles(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => System.IO.Path.GetRelativePath(root, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);

        private static bool SnapshotsEqual(Dictionary<string, string> left, Dictionary<string, string> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var hash) && hash == pair.Value);

        private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        private static void InvokePowerShell(string command)
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(command);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell 7 for junction setup.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"PowerShell junction operation failed with exit code {process.ExitCode}: {stderr.Result.Trim()} {stdout.Result.Trim()}".Trim());
        }

        public void Dispose()
        {
            foreach (var link in junctions.Keys.ToArray()) RemoveJunction(link);
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
