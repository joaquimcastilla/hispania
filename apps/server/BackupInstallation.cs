using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Hispania.Server;

// Host composition owns paths. Operational capture never changes domain records.
internal static class BackupInstallation
{
    internal const int FormatGeneration = 1;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private static readonly Meter Meter = new("Hispania.Backup", "1.0");
    private static readonly DiagnosticListener Diagnostics = new("Hispania.Backup");
    private static void Checkpoint(string name) { if (Diagnostics.IsEnabled("Checkpoint")) Diagnostics.Write("Checkpoint", name); }
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("hispania.backup.duration", "ms");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("hispania.backup.failures");
    private static readonly Histogram<long> Bytes = Meter.CreateHistogram<long>("hispania.backup.bytes", "By");
    private static readonly Histogram<long> Components = Meter.CreateHistogram<long>("hispania.backup.components");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ModuleJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["Platform:DataPath"] = "platform", ["Platform:Events:Path"] = "platform/events.sqlite",
        ["Platform:Jobs:Path"] = "platform/jobs.sqlite", ["Platform:Assets:MetadataPath"] = "platform/assets.sqlite",
        ["Platform:Audit:Path"] = "platform/audit.sqlite", ["Identity:Sqlite:Path"] = "platform/identity.sqlite",
        ["Inventory:Sqlite:Path"] = "inventory.sqlite", ["DistributedWorkers:StatePath"] = "coordinator.sqlite",
        ["DistributedWorkers:TransferPath"] = "transfers", ["Modules:ExtensionsPath"] = "extensions",
        ["Persistence:Sqlite:Path"] = "media.sqlite", ["Storage:Providers:Filesystem:Root"] = "storage",
        ["PlaybackExperiments:StatePath"] = "playback.json", ["Playback:TranscodeCachePath"] = "cache",
        ["Metadata:InventoryPath"] = "metadata-index.json"
    };
    private static readonly Dictionary<string, string> Databases = new()
    {
        ["platform/identity.sqlite"] = "Identity.Authorization", ["platform/events.sqlite"] = "Events",
        ["platform/jobs.sqlite"] = "Jobs", ["platform/assets.sqlite"] = "Assets",
        ["platform/audit.sqlite"] = "Audit", ["inventory.sqlite"] = "Inventory",
        ["coordinator.sqlite"] = "Coordinator", ["media.sqlite"] = "Media.SQLite"
    };
    private static readonly string[] PortablePrefixes =
    ["Storage:DefaultProvider", "Modules:TrustedExtensionIds:", "Modules:TrustedPublisherKeys:",
     "Modules:RevokedPublisherIds:", "Modules:RequirePublisherSignature", "Modules:PlaybackPlanner",
     "Modules:OutOfProcessResources:MaximumMemoryBytes", "Modules:OutOfProcessResources:MaximumCpuSeconds",
     "Modules:OutOfProcessResources:MaximumConcurrentRequests", "Modules:OutOfProcessResources:MaximumWindowsProcessCount",
     "Modules:OutOfProcessResources:MaximumRequestDuration", "Modules:OutOfProcessResources:MaximumCpuRatePercent",
     "Modules:OutOfProcessResources:RequireOperatingSystemLimits", "Modules:OutOfProcessResources:EnforceLinuxAddressSpaceLimit",
     "Modules:OutOfProcessRestarts:MaximumRestarts", "Modules:OutOfProcessRestarts:RecoveryWindow",
     "Modules:OutOfProcessRestarts:InitialBackoff", "Modules:OutOfProcessRestarts:MaximumBackoff", "Identity:SessionLifetimeMinutes",
     "Platform:Events:MaximumPayloadBytes", "Platform:Events:MaximumAttempts", "Platform:Events:RetryDelaySeconds",
     "Platform:Jobs:MaximumPayloadBytes", "Platform:Jobs:MaximumResultMetadataBytes", "Platform:Jobs:MaximumAttempts",
     "Platform:Jobs:RetryDelaySeconds", "Platform:Jobs:ExecutionLeaseSeconds", "Platform:Jobs:PollIntervalMilliseconds",
     "Platform:Assets:MaximumBytes", "Persistence:Provider"];

    internal sealed record Payload(string Path, string Component, long Size, string Sha256, string? Schema);
    internal sealed record Manifest(int BackupFormatGeneration, string DistributionVersion, Guid BackupId,
        DateTimeOffset CreatedUtc, string ConsistencyMode, string Provider, string StorageProvider, Payload[] Files,
        string[] RequiredComponents, string[] Reprovision, string[] Warnings);

    internal static IDisposable? Configure(IConfiguration configuration)
    {
        var configured = configuration["Backup:ManagedRoot"];
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var root = NormalizeRoot(configured); RejectLinks(root);
        var lease = Lock(root);
        try
        {
            if (File.Exists(root + ".restore-in-progress")) throw new InvalidOperationException("Incomplete restore requires explicit recovery before startup.");
            Directory.CreateDirectory(root);
            var portablePath = Path.Combine(root, "portable.json");
            if (File.Exists(portablePath))
            {
                var portable = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(portablePath))
                    ?? throw new InvalidDataException("Invalid portable configuration.");
                foreach (var (key, value) in portable)
                {
                    if (!IsPortable(key)) throw new InvalidDataException("Unsupported portable configuration key.");
                    configuration[key] = value;
                }
            }
            var layout = new Dictionary<string, string>();
            foreach (var (key, relative) in Paths)
            {
                var fallback = relative.StartsWith("platform/", StringComparison.Ordinal)
                    ? Path.Combine(configuration["Platform:DataPath"]!, Path.GetFileName(relative))
                    : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (key == "Storage:Providers:Filesystem:Root" && !string.IsNullOrWhiteSpace(configuration["Platform:Assets:ContentPath"])) fallback = configuration["Platform:Assets:ContentPath"]!;
                var legacy = key switch
                {
                    "Persistence:Sqlite:Path" => Path.Combine(root, "catalog.sqlite"),
                    "DistributedWorkers:StatePath" => Path.Combine(root, "distributed-coordinator.sqlite"),
                    "Storage:Providers:Filesystem:Root" => Path.Combine(configuration["Platform:DataPath"]!, "assets"),
                    _ => fallback
                };
                if (!File.Exists(fallback) && !Directory.Exists(fallback) && (File.Exists(legacy) || Directory.Exists(legacy))) fallback = legacy;
                var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!);
                RejectLinks(path);
                configuration[key] = path;
                layout.Add(relative, path);
            }
            if ((configuration["Storage:DefaultProvider"] ?? "filesystem") != "filesystem")
                throw new InvalidOperationException("Managed backup supports only the filesystem Storage provider.");
            var provider = configuration["Persistence:Provider"] ?? "sqlite";
            if (provider is not ("sqlite" or "postgresql")) throw new InvalidDataException("Unsupported managed persistence provider.");
            configuration["Persistence:Provider"] = provider;
            if (provider == "postgresql")
            {
                var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = configuration["Persistence:PostgreSql:ConnectionString"] ?? throw new InvalidDataException("Reprovision the PostgreSQL connection configuration.") };
                if (!connection.TryGetValue("Host", out var host) || !connection.TryGetValue("Database", out var database)) throw new InvalidDataException("PostgreSQL Host and Database must be explicit for backup.");
                var port = connection.TryGetValue("Port", out var value) ? value.ToString()! : "5432";
                WriteJson(Path.Combine(root, "postgres-source.local.json"), new Dictionary<string, string> { ["Host"] = host.ToString()!, ["Port"] = port, ["Database"] = database.ToString()! });
            }
            var values = configuration.AsEnumerable().Where(p => IsPortable(p.Key) && p.Value is not null)
                .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
            if (values.Values.Any(v => v?.Contains("PRIVATE KEY", StringComparison.Ordinal) == true)) throw new InvalidDataException("Private keys are not portable backup configuration.");
            WriteJson(portablePath, values);
            WriteJson(Path.Combine(root, "backup-layout.local.json"), layout); // Machine paths never enter the bundle.
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static bool IsPortable(string key) => !Paths.ContainsKey(key) && PortablePrefixes.Any(prefix =>
        prefix.EndsWith(':') ? key.StartsWith(prefix, StringComparison.Ordinal) : key == prefix);

    internal static void RefreshPortableConfiguration(IConfiguration configuration)
    {
        var root = NormalizeRoot(configuration["Backup:ManagedRoot"] ?? throw new InvalidDataException("Managed installation root is required."));
        var values = configuration.AsEnumerable().Where(pair => IsPortable(pair.Key) && pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (values.Values.Any(value => value?.Contains("PRIVATE KEY", StringComparison.Ordinal) == true)) throw new InvalidDataException("Private keys are not portable backup configuration.");
        WriteJson(Path.Combine(root, "portable.json"), values);
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static async Task<bool> TryCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !args[0].StartsWith("--backup-", StringComparison.Ordinal)) return false;
        var clock = Stopwatch.StartNew(); var operation = args[0];
        Console.WriteLine(JsonSerializer.Serialize(new { operation, status = "started" }));
        try
        {
            switch (operation)
            {
                case "--backup-create" when args.Length == 3:
                    await CreateAsync(args[1], args[2], cancellationToken); break;
                case "--backup-verify" when args.Length == 2:
                    await VerifyAsync(args[1], cancellationToken); break;
                case "--backup-restore" when args.Length == 3:
                    await RestoreAsync(args[1], args[2], cancellationToken); break;
                default: throw new InvalidDataException("Usage: --backup-create SOURCE_ROOT BUNDLE | --backup-verify BUNDLE | --backup-restore BUNDLE CLEAN_ROOT.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { operation, status = "completed", durationMs = clock.Elapsed.TotalMilliseconds }));
        }
        catch
        {
            Failures.Add(1, new KeyValuePair<string, object?>("operation", operation));
            Console.Error.WriteLine(JsonSerializer.Serialize(new { operation, status = "failed", reason = "Backup operation rejected or failed; source remains unchanged. Check format, integrity, clean target, installation lock and PostgreSQL tooling/configuration." }));
            Environment.ExitCode = 1;
        }
        finally { Duration.Record(clock.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation)); }
        return true;
    }

    internal static async Task CreateAsync(string source, string destination, CancellationToken token)
    {
        source = NormalizeRoot(source); destination = NormalizeRoot(destination);
        RejectLinks(source); RejectLinks(destination); RejectOverlap(source, destination);
        if (!Directory.Exists(source) || Directory.Exists(destination) || File.Exists(destination)) throw new InvalidDataException("Source or destination is invalid.");
        using var lease = Lock(source);
        if (File.Exists(source + ".restore-in-progress")) throw new InvalidDataException("Incomplete restore.");
        var portable = ReadPortable(source);
        var layout = ReadLayout(source);
        foreach (var path in layout.Values)
        {
            RejectLinks(path);
            RejectOverlap(Directory.Exists(path) ? NormalizeRoot(path) : NormalizeRoot(Path.GetDirectoryName(path)!), destination);
        }
        // Exclude a still-running pre-Phase-17 coordinator using its existing OS lock too.
        var coordinatorPath = SourcePath(source, "coordinator.sqlite", layout);
        using var coordinatorLease = new FileStream(coordinatorPath + ".coordinator.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var provider = portable.GetValueOrDefault("Persistence:Provider") ?? "sqlite";
        var required = Databases.Values.Where(v => provider == "sqlite" || v != "Media.SQLite").Concat(["Storage", "Configuration", "Extensions"]).ToList();
        if (provider == "postgresql") required.Add("Media.PostgreSQL");
        var staging = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        RestrictDirectory(staging);
        try
        {
            foreach (var (path, component) in Databases)
            {
                if (component == "Media.SQLite" && provider == "postgresql") continue;
                token.ThrowIfCancellationRequested();
                var input = SourcePath(source, path, layout); var output = SafePath(staging, path);
                if (!File.Exists(input)) throw new InvalidDataException("Required store is missing.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var original = OpenDatabase(input);
                using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output, Pooling = false }.ToString());
                snapshot.Open(); original.BackupDatabase(snapshot);
                Checkpoint("sqlite-capture");
                using (var journal = snapshot.CreateCommand()) { journal.CommandText = "PRAGMA journal_mode=DELETE"; journal.ExecuteNonQuery(); }
                CheckDatabase(output);
            }
            var warnings = Hispania.Platform.Persistence.Sqlite.SqliteAssetMetadataRepository.ValidateBackupStorage(
                Path.Combine(staging, "platform", "assets.sqlite"), SourcePath(source, "storage", layout));
            Directory.CreateDirectory(Path.Combine(staging, "storage"));
            Directory.CreateDirectory(Path.Combine(staging, "extensions"));
            foreach (var folder in new[] { "storage", "extensions" })
            {
                var input = SourcePath(source, folder, layout);
                if (Directory.Exists(input)) await CopyTreeAsync(input, Path.Combine(staging, folder), token, folder == "storage" ? "storage-copy" : null);
            }
            await CopyFileAsync(Path.Combine(source, "portable.json"), Path.Combine(staging, "portable.json"), token);
            CheckPackages(staging, token);
            if (File.Exists(SourcePath(source, "playback.json", layout)))
            {
                required.Add("Playback");
                await CopyFileAsync(SourcePath(source, "playback.json", layout), Path.Combine(staging, "playback.json"), token);
            }
            string? postgresSchema = null;
            if (provider == "postgresql")
            {
                ValidatePostgreSqlSource(source);
                var tooling = (await PostgreSqlAsync("pg_dump", ["--version"], token)).Trim();
                var serverVersion = (await PostgreSqlAsync("psql", ["--no-psqlrc", "--tuples-only", "--no-align", "--command=SHOW server_version_num"], token)).Trim();
                var toolMajorMatch = System.Text.RegularExpressions.Regex.Match(tooling, @"\b(\d+)\.\d+");
                if (!toolMajorMatch.Success || !int.TryParse(serverVersion, out var serverNumber) || int.Parse(toolMajorMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != serverNumber / 10000)
                    throw new InvalidDataException("PostgreSQL client and server major versions must match.");
                var migration = (await PostgreSqlAsync("psql", ["--no-psqlrc", "--tuples-only", "--no-align", "--command=SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\""], token)).Trim();
                Hispania.Library.Persistence.PostgreSql.PostgreSqlMigrationRunner.ValidateBackupHistory(migration);
                postgresSchema = migration + "|" + tooling;
                await PostgreSqlAsync("pg_dump", ["--format=custom", "--no-owner", "--no-acl", "--file=" + Path.Combine(staging, "media.dump")], token);
            }
            var files = await InventoryAsync(staging, token);
            files = files.Select(file => file.Path == "media.dump" ? file with { Schema = postgresSchema } : file).ToArray();
            var manifest = new Manifest(FormatGeneration, DistributionVersion(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                "offline-exclusive-installation-lock", provider, "filesystem", files, required.Order(StringComparer.Ordinal).ToArray(),
                ["PostgreSQL connection credentials", "bootstrap token", "private publisher keys", "worker certificates and credentials", "TMDB/API keys", "playback assignment salt", "external Library roots and media sources"], warnings);
            Checkpoint("before-manifest");
            WriteJson(Path.Combine(staging, "manifest.json"), manifest);
            await VerifyAsync(staging, token);
            token.ThrowIfCancellationRequested();
            Checkpoint("before-publication");
            Directory.Move(staging, destination);
            Checkpoint("backup-published");
            Bytes.Record(files.Sum(f => f.Size), new KeyValuePair<string, object?>("operation", "create")); Components.Record(required.Count, new KeyValuePair<string, object?>("operation", "create"));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal static async Task<Manifest> VerifyAsync(string bundle, CancellationToken token)
    {
        bundle = NormalizeRoot(bundle); RejectLinks(bundle);
        var manifestPath = Path.Combine(bundle, "manifest.json"); RejectLinks(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > MaximumManifestBytes) throw new InvalidDataException("Manifest is missing or oversized.");
        var manifestText = await File.ReadAllTextAsync(manifestPath, token);
        using (var document = JsonDocument.Parse(manifestText)) RejectDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestText, Json) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.BackupFormatGeneration != FormatGeneration || manifest.BackupId == Guid.Empty || !Version.TryParse(manifest.DistributionVersion, out var distribution) || distribution > Version.Parse(DistributionVersion()) ||
            manifest.ConsistencyMode != "offline-exclusive-installation-lock" || manifest.Provider is not ("sqlite" or "postgresql") || manifest.StorageProvider != "filesystem" || manifest.CreatedUtc == default || manifest.CreatedUtc.Offset != TimeSpan.Zero ||
            manifest.Files is null || manifest.RequiredComponents is null || manifest.Reprovision is null || manifest.Warnings is null)
            throw new InvalidDataException("Unsupported backup metadata.");
        if (manifest.Files.Length > 65536 || manifest.Files.Any(file => file is null || file.Size < 0) || manifest.Files.Sum(file => (decimal)file.Size) > 1024m * 1024 * 1024 * 1024)
            throw new InvalidDataException("Backup exceeds the 65536-file or 1 TiB resource policy.");
        var expected = Databases.Values.Where(v => manifest.Provider == "sqlite" || v != "Media.SQLite").Concat(["Storage", "Configuration", "Extensions"])
            .Concat(manifest.Provider == "postgresql" ? ["Media.PostgreSQL"] : Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray();
        var required = manifest.RequiredComponents.Where(c => c != "Playback").Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(required) || manifest.RequiredComponents.Distinct(StringComparer.Ordinal).Count() != manifest.RequiredComponents.Length)
            throw new InvalidDataException("Incomplete or duplicate component inventory.");
        var seen = new HashSet<string>(PathComparer);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || file.Path is null || !seen.Add(file.Path) || file.Component != ComponentFor(file.Path) || file.Size < 0 ||
                file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid payload entry.");
            var path = SafePath(bundle, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || await HashAsync(path, token) != file.Sha256)
                throw new InvalidDataException("Payload integrity failed.");
            if (Databases.ContainsKey(file.Path) && CheckDatabase(path) != file.Schema) throw new InvalidDataException("Schema metadata mismatch.");
            if (file.Path == "playback.json" && CheckPlaybackSchema(path) != file.Schema) throw new InvalidDataException("Playback schema metadata mismatch.");
            if (file.Path == "media.dump") Hispania.Library.Persistence.PostgreSql.PostgreSqlMigrationRunner.ValidateBackupMetadata(file.Schema);
        }
        var actual = EnumerateSafe(bundle).Select(path => Path.GetRelativePath(bundle, path).Replace('\\', '/')).Where(path => path != "manifest.json").ToHashSet(PathComparer);
        if (!seen.SetEquals(actual)) throw new InvalidDataException("Unlisted or missing payload.");
        foreach (var (path, component) in Databases)
            if ((manifest.Provider == "sqlite" || component != "Media.SQLite") && !seen.Contains(path)) throw new InvalidDataException("Required store missing.");
        if (!seen.Contains("portable.json") || (manifest.Provider == "postgresql" && !seen.Contains("media.dump")) ||
            (manifest.RequiredComponents.Contains("Playback") != seen.Contains("playback.json"))) throw new InvalidDataException("Required payload missing.");
        var portable = ReadPortable(bundle);
        CheckPackages(bundle, token);
        if (portable.GetValueOrDefault("Persistence:Provider") != manifest.Provider) throw new InvalidDataException("Provider metadata mismatch.");
        var warnings = Hispania.Platform.Persistence.Sqlite.SqliteAssetMetadataRepository.ValidateBackupStorage(
            Path.Combine(bundle, "platform", "assets.sqlite"), Path.Combine(bundle, "storage"));
        if (!warnings.SequenceEqual(manifest.Warnings)) throw new InvalidDataException("Storage warnings mismatch.");
        return manifest;
    }

    internal static async Task RestoreAsync(string bundle, string target, CancellationToken token)
    {
        bundle = NormalizeRoot(bundle); target = NormalizeRoot(target);
        RejectLinks(target); RejectOverlap(bundle, target);
        var manifest = await VerifyAsync(bundle, token); // No target modification before full verification.
        if (Directory.Exists(target) || File.Exists(target)) throw new InvalidDataException("Restore requires an absent clean target root.");
        var size = manifest.Files.Sum(f => checked(f.Size));
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (drive.IsReady && drive.AvailableFreeSpace < size) throw new InvalidDataException("Insufficient target capacity.");
        using var lease = Lock(target);
        if (Directory.Exists(target)) throw new InvalidDataException("Target changed during preflight.");
        var staging = target + ".restore-tmp-" + Guid.NewGuid().ToString("N");
        var marker = target + ".restore-in-progress";
        if (File.Exists(marker)) throw new InvalidDataException("Incomplete restore requires explicit recovery.");
        Directory.CreateDirectory(staging);
        RestrictDirectory(staging);
        var externalStarted = false;
        try
        {
            foreach (var file in manifest.Files)
            {
                var destination = SafePath(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(SafePath(bundle, file.Path), destination, token);
                Checkpoint("restore-staging");
                if (await HashAsync(destination, token) != file.Sha256) throw new InvalidDataException("Staged integrity failed.");
            }
            Directory.CreateDirectory(Path.Combine(staging, "storage"));
            Directory.CreateDirectory(Path.Combine(staging, "extensions"));
            token.ThrowIfCancellationRequested();
            Checkpoint("restore-before-switch");
            if (manifest.Provider == "postgresql")
            {
                // Tooling preflight and empty DB test occur before any external write.
                await PostgreSqlAsync("pg_restore", ["--version"], token);
                var empty = await PostgreSqlAsync("psql", ["--no-psqlrc", "--tuples-only", "--no-align", "--command=SELECT count(*) FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND c.relkind IN ('r','p','v','m','S','f')"], token);
                if (empty.Trim() != "0") throw new InvalidDataException("PostgreSQL restore requires an empty database.");
            }
            WriteJson(marker, new { manifest.BackupId, state = "switch-in-progress" });
            Checkpoint("restore-switch");
            if (manifest.Provider == "postgresql")
            {
                externalStarted = true;
                await PostgreSqlAsync("pg_restore", ["--exit-on-error", "--single-transaction", "--no-owner", "--no-acl", "--dbname=" + RequirePostgreSqlDatabase(), Path.Combine(staging, "media.dump")], CancellationToken.None);
                File.Delete(Path.Combine(staging, "media.dump"));
            }
            Directory.Move(staging, target); // Single local commit; cancellation is no longer honored.
            Checkpoint("restore-published");
            File.Delete(marker);
            Bytes.Record(size, new KeyValuePair<string, object?>("operation", "restore")); Components.Record(manifest.RequiredComponents.Length, new KeyValuePair<string, object?>("operation", "restore"));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (!externalStarted && !Directory.Exists(target) && File.Exists(marker)) File.Delete(marker);
        }
    }

    private static Dictionary<string, string?> ReadPortable(string root)
    {
        var path = Path.Combine(root, "portable.json"); RejectLinks(path);
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Oversized configuration.");
        var text = File.ReadAllText(path); using (var document = JsonDocument.Parse(text)) RejectDuplicateProperties(document.RootElement);
        var result = JsonSerializer.Deserialize<Dictionary<string, string?>>(text) ?? throw new InvalidDataException("Invalid configuration.");
        if (result.Keys.Any(key => !IsPortable(key))) throw new InvalidDataException("Unapproved configuration field.");
        return result;
    }
    private static Dictionary<string, string> ReadLayout(string root)
    {
        var path = Path.Combine(root, "backup-layout.local.json"); RejectLinks(path);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Oversized source layout.");
        var text = File.ReadAllText(path); using (var document = JsonDocument.Parse(text)) RejectDuplicateProperties(document.RootElement);
        var layout = JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? throw new InvalidDataException("Invalid source layout.");
        if (layout.Count != Paths.Count || !layout.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Paths.Values) || layout.Values.Any(value => !Path.IsPathFullyQualified(value)))
            throw new InvalidDataException("Incomplete source layout.");
        return layout;
    }
    private static string SourcePath(string root, string componentPath, Dictionary<string, string> layout)
        => layout.TryGetValue(componentPath, out var path) ? path : SafePath(root, componentPath);
    private static void ValidatePostgreSqlSource(string root)
    {
        var path = Path.Combine(root, "postgres-source.local.json"); RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("PostgreSQL source coordinates are required.");
        var source = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid PostgreSQL source coordinates.");
        foreach (var (key, environment) in new[] { ("Host", "PGHOST"), ("Port", "PGPORT"), ("Database", "PGDATABASE") })
            if (!source.TryGetValue(key, out var expected) || !string.Equals(expected, Environment.GetEnvironmentVariable(environment) ?? (key == "Port" ? "5432" : null), StringComparison.Ordinal))
                throw new InvalidDataException("PostgreSQL backup coordinates do not match the configured source installation.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property."); RejectDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static string DistributionVersion()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "version.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.GetProperty("distributionVersion").GetString()!;
        }
        throw new InvalidDataException("Distribution metadata is unavailable.");
    }

    internal static FileStream Lock(string root)
    {
        var path = NormalizeRoot(root) + ".maintenance.lock"; RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static string CheckDatabase(string path) => Hispania.Platform.Persistence.Sqlite.SqliteBackupInspection.Inspect(path,
        Path.GetFileName(path) == "media.sqlite" ? Hispania.Library.Persistence.Sqlite.SqliteMigrationRunner.InspectBackupHistory : null);
    private static string ComponentFor(string path)
    {
        if (Databases.TryGetValue(path, out var component)) return component;
        if (path.StartsWith("storage/", StringComparison.Ordinal)) return "Storage";
        if (path.StartsWith("extensions/", StringComparison.Ordinal)) return "Extensions";
        return path switch { "portable.json" => "Configuration", "playback.json" => "Playback", "media.dump" => "Media.PostgreSQL", _ => throw new InvalidDataException("Unsupported payload path.") };
    }
    private static async Task<Payload[]> InventoryAsync(string root, CancellationToken token)
    {
        var result = new List<Payload>();
        foreach (var path in EnumerateSafe(root).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            result.Add(new(relative, ComponentFor(relative), new FileInfo(path).Length, await HashAsync(path, token), Databases.ContainsKey(relative) ? CheckDatabase(path) : relative == "playback.json" ? CheckPlaybackSchema(path) : null));
        }
        return result.OrderBy(f => f.Component, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }
    private static string CheckPlaybackSchema(string path)
    {
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Playback state exceeds the supported backup parsing limit.");
        using var stream = File.OpenRead(path); using var document = JsonDocument.Parse(stream); RejectDuplicateProperties(document.RootElement);
        if (!document.RootElement.TryGetProperty("Experiment", out var experiment) ||
            (experiment.ValueKind != JsonValueKind.Null && experiment.GetProperty("SchemaVersion").GetInt32() != 1) ||
            !document.RootElement.TryGetProperty("Assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported playback state schema.");
        return "1";
    }
    private static void CheckPackages(string root, CancellationToken token)
    {
        var extensions = Path.Combine(root, "extensions");
        if (!Directory.Exists(extensions)) return;
        Hispania.Runtime.ModuleExtensionCatalog.ValidateBackupPackageSet(extensions);
        foreach (var path in EnumerateSafe(extensions).Where(path => Path.GetFileName(path).Equals("module.json", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Oversized installed module manifest.");
            var text = File.ReadAllText(path); using (var document = JsonDocument.Parse(text)) RejectDuplicateProperties(document.RootElement);
            var manifest = JsonSerializer.Deserialize<Hispania.ModuleSystem.Contracts.ModuleManifest>(text, ModuleJson) ?? throw new InvalidDataException("Invalid installed module manifest.");
            Hispania.Runtime.ModuleExtensionCatalog.ValidateBackupPackage(manifest, Path.GetDirectoryName(path)!);
        }
    }
    private static IEnumerable<string> EnumerateSafe(string root, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("Backup directory depth exceeds the security limit.");
        RejectLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) { foreach (var nested in EnumerateSafe(entry, depth + 1)) yield return nested; }
            else yield return entry;
        }
    }
    private static async Task CopyTreeAsync(string source, string target, CancellationToken token, string? checkpoint = null)
    {
        foreach (var path in EnumerateSafe(source))
        {
            var destination = SafePath(target, Path.GetRelativePath(source, path).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileAsync(path, destination, token, checkpoint);
        }
    }
    private static async Task CopyFileAsync(string source, string target, CancellationToken token, string? checkpoint = null)
    {
        RejectLinks(source); RejectLinks(target);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (checkpoint is not null) Checkpoint(checkpoint);
        }
        await output.FlushAsync(token); output.Flush(true);
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
    private static void WriteJson<T>(string path, T value)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Json); stream.Flush(true);
    }
    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
            relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' '))) throw new InvalidDataException("Unsafe bundle path.");
        var path = Path.GetFullPath(Path.Combine(root, relative)); RejectLinks(path); return path;
    }
    private static void RejectOverlap(string first, string second)
    {
        var firstPrefix = Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar;
        var secondPrefix = Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar;
        if (first.Equals(second, PathComparison) || first.StartsWith(secondPrefix, PathComparison) ||
            second.StartsWith(firstPrefix, PathComparison)) throw new InvalidDataException("Roots must not overlap.");
    }
    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private static void RestrictDirectory(string root)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var current = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            security.SetOwner(current);
            foreach (var sid in new[] { current, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
        }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links and reparse points are unsupported.");
    }
    private static string RequirePostgreSqlDatabase() => Environment.GetEnvironmentVariable("PGDATABASE") is { Length: > 0 } value ? value : throw new InvalidDataException("Reprovision PGDATABASE and libpq connection settings.");
    private static async Task<string> PostgreSqlAsync(string tool, string[] arguments, CancellationToken token)
    {
        RequirePostgreSqlDatabase();
        var configured = Environment.GetEnvironmentVariable("HISPANIA_" + tool.ToUpperInvariant() + "_PATH") ?? tool;
        var info = new ProcessStartInfo(configured) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidDataException("PostgreSQL tooling unavailable.");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); await error; if (process.ExitCode != 0) throw new InvalidDataException("PostgreSQL operation failed."); return await output; }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
    }
}
