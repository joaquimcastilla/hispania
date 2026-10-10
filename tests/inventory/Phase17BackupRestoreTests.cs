using System.Text.Json;
using System.Text.Json.Nodes;
using Hispania.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hispania.Inventory.Tests;

[Trait("Category", "Phase17BackupRestore")]
public sealed class Phase17BackupRestoreTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public Phase17BackupRestoreTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [Fact]
    public void DistributionPrereleaseRemainsCompatibleWithTheSameDataVersionLine()
    {
        Assert.True(BackupInstallation.IsDistributionVersionCompatible("0.7.0", "0.7.0-rc.1"));
        Assert.True(BackupInstallation.IsDistributionVersionCompatible("0.7.0-rc.1", "0.7.0"));
        Assert.True(BackupInstallation.IsDistributionVersionCompatible("0.6.9", "0.7.0-rc.1"));
        Assert.False(BackupInstallation.IsDistributionVersionCompatible("0.8.0", "0.7.0-rc.1"));
        Assert.False(BackupInstallation.IsDistributionVersionCompatible("not-a-version", "0.7.0-rc.1"));
    }

    [Fact]
    public async Task HistoricalPopulatedInstallationRestoresOnlyFromBundleWithSemanticEquivalence()
    {
        using var fixture = new Installation();
        var before = Snapshot(fixture.Source);
        var playback = File.ReadAllText(Path.Combine(fixture.Source, "playback.json"));
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        var manifest = await BackupInstallation.VerifyAsync(fixture.Bundle, default);
        Assert.Equal(1, manifest.BackupFormatGeneration); Assert.NotEqual(Guid.Empty, manifest.BackupId);
        Assert.Equal(12, manifest.RequiredComponents.Length);
        SqliteConnection.ClearAllPools(); Directory.Delete(fixture.Source, true);
        await BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default);
        Assert.Equal(before, Snapshot(fixture.Target));
        Assert.Equal(playback, File.ReadAllText(Path.Combine(fixture.Target, "playback.json")));
        var second = Path.Combine(fixture.Root, "second-target");
        await BackupInstallation.RestoreAsync(fixture.Bundle, second, default);
        Assert.Equal(before, Snapshot(second));
        Assert.False(File.Exists(fixture.Target + ".restore-in-progress"));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("format")]
    [InlineData("hash")]
    [InlineData("sqlite")]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("duplicate-file")]
    [InlineData("duplicate-component")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("unlisted")]
    [InlineData("oversized")]
    [InlineData("duplicate-property")]
    [InlineData("playback-schema")]
    [InlineData("negative-size")]
    [InlineData("overflow-size")]
    [InlineData("wrong-size")]
    [InlineData("unknown-required")]
    public async Task HostileOrIncompleteBundleFailsBeforeAnyTargetModification(string corruption)
    {
        using var fixture = new Installation();
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        var manifestPath = Path.Combine(fixture.Bundle, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var first = manifest["Files"]![0]!;
        switch (corruption)
        {
            case "manifest": File.WriteAllText(manifestPath, "{"); break;
            case "oversized": File.WriteAllText(manifestPath, new string(' ', 4 * 1024 * 1024 + 1)); break;
            case "duplicate-property": File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"BackupFormatGeneration\": 1", "\"BackupFormatGeneration\": 1, \"BackupFormatGeneration\": 1")); break;
            case "sqlite": File.AppendAllText(Path.Combine(fixture.Bundle, "platform", "identity.sqlite"), "tampered"); break;
            case "bytes": File.AppendAllText(Directory.GetFiles(Path.Combine(fixture.Bundle, "storage"))[0], "tampered"); break;
            case "missing": File.Delete(Path.Combine(fixture.Bundle, "platform", "jobs.sqlite")); break;
            case "unlisted": File.WriteAllText(Path.Combine(fixture.Bundle, "unexpected.txt"), "unlisted"); break;
            case "playback-schema":
                var playbackPath = Path.Combine(fixture.Bundle, "playback.json"); var playback = JsonNode.Parse(File.ReadAllText(playbackPath))!;
                playback["Experiment"]!["SchemaVersion"] = 2; File.WriteAllText(playbackPath, playback.ToJsonString());
                var entry = manifest["Files"]!.AsArray().Single(node => node!["Path"]!.GetValue<string>() == "playback.json")!;
                entry["Size"] = new FileInfo(playbackPath).Length; entry["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(playbackPath)));
                File.WriteAllText(manifestPath, manifest.ToJsonString()); break;
            default:
                switch (corruption)
                {
                    case "format": manifest["BackupFormatGeneration"] = 2; break;
                    case "negative-size": first["Size"] = -1; break;
                    case "overflow-size": first["Size"] = System.Text.Json.Nodes.JsonNode.Parse("9223372036854775808"); break;
                    case "wrong-size": first["Size"] = 1; break;
                    case "unknown-required": manifest["RequiredComponents"]!.AsArray().Add("Unknown.Required"); break;
                    case "hash": first["Sha256"] = "not-a-hash"; break;
                    case "duplicate-file": manifest["Files"]!.AsArray().Add(first.DeepClone()); break;
                    case "duplicate-component": manifest["RequiredComponents"]!.AsArray().Add("Assets"); break;
                    case "traversal": first["Path"] = "../outside.sqlite"; break;
                    case "absolute": first["Path"] = "C:/outside.sqlite"; break;
                }
                File.WriteAllText(manifestPath, manifest.ToJsonString()); break;
        }
        var sentinel = Path.Combine(fixture.Root, "sentinel"); File.WriteAllText(sentinel, "unchanged");
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default));
        Assert.False(Directory.Exists(fixture.Target)); Assert.False(File.Exists(fixture.Target + ".restore-in-progress"));
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task SqliteBackupCapturesCommittedPagesStillInWal()
    {
        using var fixture = new Installation();
        var path = Path.Combine(fixture.Source, "platform", "audit.sqlite");
        using var db = Open(path); using var q = db.CreateCommand();
        q.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE wal_probe(value TEXT); INSERT INTO wal_probe VALUES('committed-in-wal');"; q.ExecuteNonQuery();
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        using var snapshot = Open(Path.Combine(fixture.Bundle, "platform", "audit.sqlite")); using var query = snapshot.CreateCommand();
        query.CommandText = "SELECT value FROM wal_probe"; Assert.Equal("committed-in-wal", query.ExecuteScalar());
        Assert.False(File.Exists(Path.Combine(fixture.Bundle, "platform", "audit.sqlite-wal")));
    }

    [Fact]
    public async Task FailedAndCancelledBackupLeavesLiveStateAndBarrierIntact()
    {
        using var fixture = new Installation(); var before = Snapshot(fixture.Source);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, cancellation.Token));
        Assert.Equal(before, Snapshot(fixture.Source)); Assert.False(Directory.Exists(fixture.Bundle));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "*.tmp-*"));
        using (BackupInstallation.Lock(fixture.Source))
            await Assert.ThrowsAsync<IOException>(() => BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default));
        File.Delete(Path.Combine(fixture.Source, "storage", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.blob"));
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default));
        Assert.Equal(before.Where(v => !v.StartsWith("storage/", StringComparison.Ordinal)), Snapshot(fixture.Source).Where(v => !v.StartsWith("storage/", StringComparison.Ordinal)));
        using var released = BackupInstallation.Lock(fixture.Source);
    }

    [Fact]
    public async Task OverlapPopulatedTargetAndCancelledRestoreAreRejected()
    {
        using var fixture = new Installation();
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.CreateAsync(fixture.Source, Path.Combine(fixture.Source, "backups"), default));
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.CreateAsync(fixture.Source + Path.DirectorySeparatorChar, Path.Combine(fixture.Source, "backups"), default));
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.CreateAsync(Path.GetPathRoot(fixture.Source)!, fixture.Bundle, default));
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        Directory.CreateDirectory(fixture.Target); File.WriteAllText(Path.Combine(fixture.Target, "old"), "preserve");
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.Target, "old")));
        Directory.Delete(fixture.Target, true);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, cancellation.Token));
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Fact]
    public void PortableConfigurationExcludesSecretsAndStartupHonorsRestoreMarker()
    {
        using var fixture = new Installation();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:ManagedRoot"] = fixture.Target, ["Identity:Bootstrap:Token"] = "excluded-bootstrap",
            ["Persistence:PostgreSql:ConnectionString"] = "Password=excluded-db", ["Modules:AdminToken"] = "excluded-admin",
            ["PlaybackExperiments:AssignmentSalt"] = "excluded-salt", ["Modules:TrustedPublisherKeys:public-test"] = "public-test-key",
            ["Modules:OutOfProcessResources:Password"] = "excluded-resource-secret", ["Modules:OutOfProcessRestarts:Secret"] = "excluded-restart-secret",
            ["Platform:Events:Password"] = "excluded-event-secret", ["Platform:Jobs:Secret"] = "excluded-job-secret",
            ["Modules:RevokedPublisherIds:0"] = "revoked-test-publisher"
        }).Build();
        using (BackupInstallation.Configure(configuration)) { }
        var text = File.ReadAllText(Path.Combine(fixture.Target, "portable.json"));
        Assert.DoesNotContain("excluded-", text); Assert.Contains("public-test-key", text); Assert.Contains("revoked-test-publisher", text);
        File.WriteAllText(fixture.Target + ".restore-in-progress", "interrupted");
        Assert.Throws<InvalidOperationException>(() => BackupInstallation.Configure(configuration));
    }

    [Fact]
    public async Task OrphansAreRecordedAndLargeAssetUsesStreamingPayloads()
    {
        using var fixture = new Installation();
        var orphan = Path.Combine(fixture.Source, "storage", "orphan.blob");
        using (var file = File.Create(orphan)) { var chunk = new byte[81920]; for (var i = 0; i < 256; i++) file.Write(chunk); }
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        var manifest = await BackupInstallation.VerifyAsync(fixture.Bundle, default);
        Assert.Single(manifest.Warnings); Assert.Contains(manifest.Files, f => f.Size == 81920L * 256);
        await BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default);
        Assert.Equal(new FileInfo(orphan).Length, new FileInfo(Path.Combine(fixture.Target, "storage", "orphan.blob")).Length);
    }

    [Fact]
    public async Task ExistingScatteredSourcePathsArePreservedAndRemapToCanonicalCleanTarget()
    {
        using var fixture = new Installation(); var before = Snapshot(fixture.Source);
        var control = Path.Combine(fixture.Root, "control");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:ManagedRoot"] = control, ["Platform:DataPath"] = Path.Combine(fixture.Source, "platform"),
            ["Inventory:Sqlite:Path"] = Path.Combine(fixture.Source, "inventory.sqlite"),
            ["DistributedWorkers:StatePath"] = Path.Combine(fixture.Source, "coordinator.sqlite"),
            ["Persistence:Sqlite:Path"] = Path.Combine(fixture.Source, "media.sqlite"),
            ["Storage:Providers:Filesystem:Root"] = Path.Combine(fixture.Source, "storage")
        }).Build();
        using (BackupInstallation.Configure(configuration)) { }
        Assert.Equal(before, Snapshot(fixture.Source));
        using (var legacyCoordinator = new FileStream(Path.Combine(fixture.Source, "coordinator.sqlite.coordinator.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => BackupInstallation.CreateAsync(control, fixture.Bundle, default));
        await BackupInstallation.CreateAsync(control, fixture.Bundle, default);
        Assert.DoesNotContain(fixture.Source.Replace("\\", "\\\\", StringComparison.Ordinal), File.ReadAllText(Path.Combine(fixture.Bundle, "manifest.json")));
        Directory.Delete(fixture.Source, true); Directory.Delete(control, true);
        await BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default);
        Assert.Equal(before, Snapshot(fixture.Target));
    }

    [Fact]
    public async Task InstalledPackageWithMissingPayloadCannotProduceSuccessfulBackup()
    {
        using var fixture = new Installation(); var before = Snapshot(fixture.Source);
        var packageRoot = Path.Combine(fixture.Source, "extensions", "incomplete"); Directory.CreateDirectory(packageRoot);
        var manifest = new Hispania.ModuleSystem.Contracts.ModuleManifest
        {
            Id = "test.incomplete", Name = "Incomplete", Version = "1.0.0", Implements = [], MinimumHostVersion = "1.0.0",
            Runtime = "dotnet-inprocess-trusted", EntryAssembly = "missing.dll", EntryType = "test.Missing",
            Files = new Dictionary<string, string> { ["missing.dll"] = new string('0', 64) }
        };
        File.WriteAllText(Path.Combine(packageRoot, "module.json"), JsonSerializer.Serialize(manifest));
        await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default));
        Assert.False(Directory.Exists(fixture.Bundle)); Assert.Equal(before, Snapshot(fixture.Source));
    }

    [Fact]
    public async Task SymbolicAliasesAreRejectedBeforeCaptureOrRestore()
    {
        using var fixture = new Installation();
        var alias = Path.Combine(fixture.Root, "alias");
        try { Directory.CreateSymbolicLink(alias, fixture.Source); }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is UnauthorizedAccessException or IOException)
        {
            output.WriteLine("COVERAGE LIMITATION: Windows runner cannot create a symbolic link; no symlink security assertion executed.");
            return;
        }
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.CreateAsync(alias, fixture.Bundle, default));
            Assert.False(Directory.Exists(fixture.Bundle));
            await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
            await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.RestoreAsync(fixture.Bundle, Path.Combine(alias, "restored"), default));
            Assert.False(Directory.Exists(Path.Combine(fixture.Source, "restored")));
            File.CreateSymbolicLink(Path.Combine(fixture.Bundle, "linked-payload"), Path.Combine(fixture.Source, "portable.json"));
            await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default));
            Assert.False(Directory.Exists(fixture.Target));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task RestoreAndStartupRespectTheSameInstallationExclusion()
    {
        using var fixture = new Installation();
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        using (BackupInstallation.Lock(fixture.Target))
        {
            await Assert.ThrowsAsync<IOException>(() => BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default));
            Assert.Throws<IOException>(() => BackupInstallation.Configure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:ManagedRoot"] = fixture.Target }).Build()));
            Assert.False(Directory.Exists(fixture.Target));
        }
        await BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default);
        Assert.Equal(Snapshot(fixture.Source), Snapshot(fixture.Target));
    }

    internal static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
    }
    internal static string[] Snapshot(string root)
    {
        var rows = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.sqlite", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var db = Open(path); using (var integrity = db.CreateCommand()) { integrity.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", integrity.ExecuteScalar()); integrity.CommandText = "PRAGMA foreign_key_check"; using var violations = integrity.ExecuteReader(); Assert.False(violations.Read()); }
            using var tablesQuery = db.CreateCommand(); tablesQuery.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            var tables = new List<string>(); using (var r = tablesQuery.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));
            foreach (var table in tables)
            {
                using var q = db.CreateCommand(); q.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
                using var r = q.ExecuteReader();
                while (r.Read()) rows.Add(Path.GetRelativePath(root, path).Replace('\\', '/') + ":" + table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i).ToString())));
            }
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "storage"))) rows.Add("storage/" + Path.GetFileName(file) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
    internal sealed class Installation : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "hispania-phase17-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source");
        internal string Target => Path.Combine(Root, "target");
        internal string Bundle => Path.Combine(Root, "bundle");
        internal Installation()
        {
            Directory.CreateDirectory(Path.Combine(Source, "platform")); Directory.CreateDirectory(Path.Combine(Source, "storage"));
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Hispania.sln"))) repo = repo.Parent;
            var corpus = Path.Combine(repo!.FullName, "compatibility", "data", "0.7");
            foreach (var (input, output) in new[] { ("identity-authorization.sqlite", "platform/identity.sqlite"), ("events.sqlite", "platform/events.sqlite"), ("jobs.sqlite", "platform/jobs.sqlite"), ("assets.sqlite", "platform/assets.sqlite"), ("audit.sqlite", "platform/audit.sqlite"), ("inventory.sqlite", "inventory.sqlite"), ("coordinator.sqlite", "coordinator.sqlite"), ("media-library.sqlite", "media.sqlite") }) File.Copy(Path.Combine(corpus, input), Path.Combine(Source, output));
            foreach (var file in Directory.GetFiles(corpus, "*.blob")) File.Copy(file, Path.Combine(Source, "storage", Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(Source, "portable.json"), "{\"Persistence:Provider\":\"sqlite\"}");
            File.Copy(Path.Combine(corpus, "playback-experiment.json"), Path.Combine(Source, "playback.json"));
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
