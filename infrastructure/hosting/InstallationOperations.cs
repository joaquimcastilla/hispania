using System.Diagnostics;
using System.Text.Json;

namespace Hispania.Hosting;

public sealed record DistributionMigrationPlan(
    string SourceVersion,
    string SourceGitSha,
    string SourceRid,
    string TargetVersion,
    string TargetGitSha,
    string TargetRid,
    string TargetManifestSha256,
    string Persistence,
    string BackupRoot,
    long RequiredFreeBytes,
    long AvailableFreeBytes,
    string[] PreservedInstallationPaths,
    string[] PreservedExternalPaths,
    string[] RemovedRuntimeComponents,
    string[] Warnings);

public static class InstallationOperations
{
    private const string OfficialCompleteRc1Sha = "9b5abefe4c6b631360d505587748aedcb6c522d0";

    public static FileStream Lock(string home)
    {
        ProductionInstallation.RejectLinks(home);
        var run = Path.Combine(home, "run");
        if (!Directory.Exists(run)) throw new DeploymentException(6, "Installation run/ directory is missing; provision the installation first.");
        try { return new FileStream(Path.Combine(run, "installation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DeploymentException(6, "Installation is running or another operation holds the exclusive lock; stop the service first."); }
    }

    public static void Install(string artifact, string home, string role)
    {
        RequireAbsolute(home); RequireAbsolute(artifact);
        RejectOverlap(artifact, home);
        var manifest = ArtifactManifest.Verify(artifact);
        RequirePlatform(manifest);
        ProductionConfiguration.Require(role is "server" or "worker", "role", "use server or worker");
        ProductionConfiguration.Require(manifest.DistributionType != "platform-only" || role == "server", "role", "Platform-only artifacts install only the server role");
        ProductionInstallation.RejectLinks(home);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(home, "run"));
        using var lease = Lock(home);
        if (Directory.Exists(Path.Combine(home, "app")) || File.Exists(Path.Combine(home, "config", "production.json"))) throw new DeploymentException(6, "Installation already exists; use the offline upgrade procedure.");
        foreach (var directory in new[] { "config", "secrets", "state", "logs", "backups" }) Directory.CreateDirectory(Path.Combine(home, directory));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(home, "secrets"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CopyTree(artifact, Path.Combine(home, "app.new"));
        _ = ArtifactManifest.Verify(Path.Combine(home, "app.new"));
        MoveDirectory(Path.Combine(home, "app.new"), Path.Combine(home, "app"));
        File.WriteAllText(Path.Combine(home, "config", "production.json"), JsonSerializer.Serialize(new ProductionConfiguration { Role = role }, ProductionConfiguration.Json));
        InstallationMetadata.Write(home);
    }

    public static async Task<string> UpgradeAsync(string artifact, string home, CancellationToken token = default)
        => await UpgradeCoreAsync(artifact, home, allowCompleteRc1Migration: false, null, null, token);

    public static DistributionMigrationPlan PlanCompleteRc1ToPlatformOnly(string artifact, string home)
    {
        RequireAbsolute(home); RequireAbsolute(artifact);
        RejectOverlap(artifact, home);
        var source = ArtifactManifest.Verify(Path.Combine(home, "app"));
        var target = ArtifactManifest.Verify(artifact);
        ValidateRc1Migration(source, target);
        InstallationMetadata.ValidateManifestBinding(home);
        var configuration = ValidateMigrationConfigurationAndLayout(home);
        if (File.Exists(Path.Combine(home, "run", "upgrade-in-progress.json")) || Directory.Exists(Path.Combine(home, "app.new")) || Directory.Exists(Path.Combine(home, "app.old")))
            throw new DeploymentException(6, "Migration preflight found retained upgrade/recovery evidence; inspect it before continuing.");
        RequireLockAvailable(Path.Combine(home, "run", "installation.lock"), "A HISPANIA process or service is active; stop it before migration.");
        var maintenanceLock = Path.Combine(home, "state") + ".maintenance.lock";
        if (File.Exists(maintenanceLock)) RequireLockAvailable(maintenanceLock, "A state maintenance operation is active; wait before migration.");
        var (requiredFreeBytes, availableFreeBytes) = RequireMigrationSpace(target, artifact, Path.Combine(home, "state"), home);
        var persistence = configuration.Persistence;
        var externalPaths = configuration.LibraryRoots.Where(path => !IsWithin(path, home)).Distinct(StringComparer.Ordinal).ToArray();
        return new DistributionMigrationPlan(source.DistributionVersion, source.GitSha, source.TargetRid,
            target.DistributionVersion, target.GitSha, target.TargetRid, ManifestSha256(artifact), persistence, Path.Combine(home, "backups"), requiredFreeBytes, availableFreeBytes,
            ["app (replaced by Platform-only binaries)", "config", "secrets", "state", "logs", "backups"],
            externalPaths, ["Media runtime", "Inventory runtime", "FFmpeg runtime", "complete Server/Worker binaries"],
            ["Media/Inventory state is retained in place and is not mounted by Platform-only.",
             "External library roots are retained in place and are not copied or modified.",
             "Service-level environment overrides are outside the HOME configuration file and must be reviewed by the operator.",
             "run/upgrade markers and last-upgrade recovery evidence are written by this operation."]);
    }

    public static async Task<string> MigrateCompleteRc1ToPlatformOnlyAsync(string artifact, string home, string confirmedTargetManifestSha256, CancellationToken token = default)
        => await MigrateCompleteRc1ToPlatformOnlyCoreAsync(artifact, home, confirmedTargetManifestSha256, null, token);

    internal static async Task<string> MigrateCompleteRc1ToPlatformOnlyWithCheckpointsAsync(string artifact, string home, string confirmedTargetManifestSha256, Action<string> checkpoint, CancellationToken token = default)
        => await MigrateCompleteRc1ToPlatformOnlyCoreAsync(artifact, home, confirmedTargetManifestSha256, checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)), token);

    private static async Task<string> MigrateCompleteRc1ToPlatformOnlyCoreAsync(string artifact, string home, string confirmedTargetManifestSha256, Action<string>? checkpoint, CancellationToken token)
    {
        RequireAbsolute(home); RequireAbsolute(artifact);
        RejectOverlap(artifact, home);
        var target = ArtifactManifest.Verify(artifact);
        RequirePlatform(target);
        var actualTargetManifestSha = ManifestSha256(artifact);
        if (string.IsNullOrWhiteSpace(confirmedTargetManifestSha256) || confirmedTargetManifestSha256.Length != 64 || !confirmedTargetManifestSha256.All(Uri.IsHexDigit) ||
            !actualTargetManifestSha.Equals(confirmedTargetManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException(6, "Migration confirmation must equal the target manifest SHA-256 shown by the inspected plan.");
        var app = Path.Combine(home, "app");
        if (Directory.Exists(app))
        {
            using (var lease = Lock(home))
            {
                var installed = ArtifactManifest.Verify(app);
                var recordPath = Path.Combine(home, "run", "last-upgrade.json");
                if (installed.DistributionType == "platform-only" && File.Exists(recordPath))
                {
                    InstallationMetadata.ValidateManifestBinding(home);
                    using var record = JsonDocument.Parse(File.ReadAllText(recordPath));
                    var root = record.RootElement;
                    if (root.TryGetProperty("operation", out var operation) && operation.GetString() == "distribution-migration" &&
                        root.TryGetProperty("targetGitSha", out var targetSha) && targetSha.GetString() == target.GitSha &&
                        root.TryGetProperty("targetManifestSha256", out var targetManifestSha) && targetManifestSha.GetString()?.Equals(actualTargetManifestSha, StringComparison.OrdinalIgnoreCase) == true &&
                        root.TryGetProperty("backup", out var backupValue) && backupValue.GetString() is { } backup)
                    {
                        var old = Path.Combine(home, "app.old");
                        var oldManifest = ArtifactManifest.Verify(old);
                        ValidateRc1Migration(oldManifest, installed);
                        await BackupCommandAsync(old, ["--backup-verify", backup], token);
                        return backup;
                    }
                }
            }
        }
        return await UpgradeCoreAsync(artifact, home, allowCompleteRc1Migration: true, actualTargetManifestSha, checkpoint, token);
    }

    private static async Task<string> UpgradeCoreAsync(string artifact, string home, bool allowCompleteRc1Migration, string? targetManifestSha256, Action<string>? checkpoint, CancellationToken token)
    {
        RequireAbsolute(home); RequireAbsolute(artifact);
        RejectOverlap(artifact, home);
        var candidate = ArtifactManifest.Verify(artifact);
        RequirePlatform(candidate);
        using var lease = Lock(home);
        var marker = Path.Combine(home, "run", "upgrade-in-progress.json");
        if (File.Exists(marker) || Directory.Exists(Path.Combine(home, "app.new")) || Directory.Exists(Path.Combine(home, "app.old"))) throw new DeploymentException(6, "An earlier upgrade requires explicit recovery; preserve app.old and backups.");
        var app = Path.Combine(home, "app");
        var installed = ArtifactManifest.Verify(app);
        var isMigration = installed.DistributionType != candidate.DistributionType;
        if (isMigration)
        {
            if (!allowCompleteRc1Migration) ProductionConfiguration.Require(false, "distributionType", "upgrade cannot switch between complete and Platform-only distributions; use the explicit RC1 migration operation");
            ValidateRc1Migration(installed, candidate);
            InstallationMetadata.ValidateManifestBinding(home);
            _ = ValidateMigrationConfigurationAndLayout(home);
        }
        else if (allowCompleteRc1Migration)
        {
            throw new DeploymentException(6, "The explicit migration operation requires the official Complete RC1 source and a Platform-only target.");
        }
        ProductionConfiguration.Require(installed.TargetRid == candidate.TargetRid, "targetRid", "upgrade artifact must match the installed target RID");
        var state = Path.Combine(home, "state");
        using (StateLock(state)) { } // Stop verified before staging and backup.
        if (isMigration) _ = RequireMigrationSpace(candidate, artifact, state, home);
        var bundle = Path.Combine(home, "backups", "pre-upgrade-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase = "staging", operation = isMigration ? "distribution-migration" : "upgrade", backup = bundle }, ProductionConfiguration.Json));
        CopyTree(artifact, Path.Combine(home, "app.new"), checkpoint is null ? null : _ => checkpoint("staging-file-copied"));
        checkpoint?.Invoke("staging-complete");
        _ = ArtifactManifest.Verify(Path.Combine(home, "app.new"));
        if (isMigration && !ManifestSha256(Path.Combine(home, "app.new")).Equals(targetManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException(6, "Staged Platform-only manifest differs from the inspected migration plan; preserve app.new and recover before startup.");
        checkpoint?.Invoke("before-backup");
        await BackupCommandAsync(app, ["--backup-create", state, bundle], token);
        checkpoint?.Invoke("after-backup-create");
        checkpoint?.Invoke("during-state-verification");
        await BackupCommandAsync(app, ["--backup-verify", bundle], token);
        checkpoint?.Invoke("after-backup-verified");
        using var stateLease = StateLock(state);
        checkpoint?.Invoke("before-switch");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase = "switching", operation = isMigration ? "distribution-migration" : "upgrade", backup = bundle }, ProductionConfiguration.Json));
        MoveDirectory(app, Path.Combine(home, "app.old"));
        checkpoint?.Invoke("during-switch-after-old-moved");
        MoveDirectory(Path.Combine(home, "app.new"), app);
        checkpoint?.Invoke("after-switch");
        InstallationMetadata.Write(home);
        checkpoint?.Invoke("after-metadata-write");
        File.WriteAllText(Path.Combine(home, "run", "last-upgrade.json"), JsonSerializer.Serialize(new { operation = isMigration ? "distribution-migration" : "upgrade", sourceDistribution = installed.DistributionType, targetDistribution = candidate.DistributionType, sourceGitSha = installed.GitSha, targetGitSha = candidate.GitSha, targetManifestSha256 = targetManifestSha256, backup = bundle, newHostStarted = false, migrations = "unknown after the new host starts; restore is required for rollback after startup" }, ProductionConfiguration.Json));
        File.Delete(marker);
        return bundle;
    }

    private static void ValidateRc1Migration(ArtifactManifest source, ArtifactManifest target)
    {
        ProductionConfiguration.Require(source.DistributionType == "complete" && source.DistributionVersion == "0.7.0-rc.1" &&
            source.GitSha.Equals(OfficialCompleteRc1Sha, StringComparison.OrdinalIgnoreCase) && !source.SourceDirty,
            "migrationSource", "only the verified official HISPANIA 0.7.0-rc.1 Complete artifact is supported");
        ProductionConfiguration.Require(target.DistributionType == "platform-only" && target.DistributionVersion == source.DistributionVersion &&
            target.TargetRid == source.TargetRid,
            "migrationTarget", "use a same-version Platform-only artifact for the installed x64 RID");
        ProductionConfiguration.Require(target.TargetRid is "win-x64" or "linux-x64", "migrationRid", "RC1 distribution migration is supported only on x64");
    }

    private static void RequireLockAvailable(string path, string message)
    {
        try { using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DeploymentException(6, message); }
        catch (UnauthorizedAccessException exception) { throw new DeploymentException(6, "Migration preflight cannot verify installation lock permissions; no files were changed.", exception); }
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(basePath, comparison) || full.StartsWith(basePath + Path.DirectorySeparatorChar, comparison);
    }

    private static string ManifestSha256(string artifact)
    {
        using var stream = File.OpenRead(Path.Combine(artifact, "artifact-manifest.json"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static ProductionConfiguration ValidateMigrationConfigurationAndLayout(string home)
    {
        var configuration = ProductionConfiguration.Read(Path.Combine(home, "config", "production.json"), []);
        configuration.Validate("server");
        if (configuration.Persistence != "sqlite")
            throw new DeploymentException(6, "Complete RC1 PostgreSQL installations cannot migrate to this Platform-only target; Platform-only currently requires SQLite. No files were changed.");
        foreach (var name in new[] { "config", "secrets", "state", "backups", "logs", "run" })
        {
            var directory = Path.Combine(home, name);
            ProductionInstallation.RejectLinks(directory);
            if (!Directory.Exists(directory)) throw new DeploymentException(6, $"Migration preflight requires the installation directory '{name}'; no files were changed.");
        }
        if (configuration.LibraryRoots.Length == 0 || configuration.LibraryRoots.Any(root => !Directory.Exists(root)))
            throw new DeploymentException(6, "Migration preflight requires every Complete RC1 library root to be mounted and accessible; no files were changed.");
        return configuration;
    }

    private static (long Required, long Available) RequireMigrationSpace(ArtifactManifest target, string targetRoot, string state, string home)
    {
        try
        {
            long targetBytes = 0;
            foreach (var relative in target.Files.Keys) targetBytes = checked(targetBytes + new FileInfo(Path.Combine(targetRoot, relative)).Length);
            var stateBytes = DirectoryBytes(state);
            var required = checked(targetBytes + stateBytes + Math.Max(64L * 1024 * 1024, stateBytes / 10));
            var root = Path.GetPathRoot(Path.GetFullPath(home));
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("Installation volume is unavailable.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new IOException("Installation volume is not ready.");
            if (drive.AvailableFreeSpace < required)
                throw new DeploymentException(6, $"Migration requires at least {required} free bytes for staging and a verified state backup; only {drive.AvailableFreeSpace} are available. No files were changed.");
            return (required, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or OverflowException or UnauthorizedAccessException)
        { throw new DeploymentException(6, "Migration preflight cannot estimate target staging space; no files were changed.", exception); }
    }

    private static long DirectoryBytes(string root)
    {
        long total = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("State contains a reparse point or symbolic link.");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            else total = checked(total + new FileInfo(entry).Length);
        }
        return total;
    }

    public static async Task RestoreRollbackAsync(string home, string bundle, CancellationToken token = default)
        => await RestoreRollbackCoreAsync(home, bundle, null, token);

    internal static async Task RestoreRollbackWithCheckpointsAsync(string home, string bundle, Action<string> checkpoint, CancellationToken token = default)
        => await RestoreRollbackCoreAsync(home, bundle, checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)), token);

    private static async Task RestoreRollbackCoreAsync(string home, string bundle, Action<string>? checkpoint, CancellationToken token)
    {
        RequireAbsolute(home); RequireAbsolute(bundle);
        using var lease = Lock(home);
        var old = Path.Combine(home, "app.old");
        _ = ArtifactManifest.Verify(old);
        var state = Path.Combine(home, "state");
        using (StateLock(state)) { }
        await BackupCommandAsync(old, ["--backup-verify", bundle], token);
        var restored = Path.Combine(home, "state.restored-" + Guid.NewGuid().ToString("N"));
        // Restore targets a fresh root. Failed/current data are retained for inspection.
        await BackupCommandAsync(old, ["--backup-restore", bundle, restored], token);
        try { checkpoint?.Invoke("rollback-restored-state-prepared"); }
        catch { if (Directory.Exists(restored)) Directory.Delete(restored, recursive: true); throw; }
        using var stateLease = StateLock(state);
        var marker = Path.Combine(home, "run", "upgrade-in-progress.json");
        var failedState = "state.failed-" + Guid.NewGuid().ToString("N");
        var failedApp = "app.failed-" + Guid.NewGuid().ToString("N");
        var restoredName = Path.GetFileName(restored);
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase = "rollback", backup = bundle, restored = restoredName, failedState, failedApp }, ProductionConfiguration.Json));
        checkpoint?.Invoke("rollback-started");
        MoveDirectory(state, Path.Combine(home, failedState));
        checkpoint?.Invoke("rollback-state-moved");
        MoveDirectory(restored, state);
        checkpoint?.Invoke("rollback-restored-state-installed");
        var app = Path.Combine(home, "app");
        if (Directory.Exists(app)) MoveDirectory(app, Path.Combine(home, failedApp));
        checkpoint?.Invoke("rollback-app-moved");
        MoveDirectory(old, app);
        checkpoint?.Invoke("rollback-source-app-installed");
        InstallationMetadata.Write(home);
        checkpoint?.Invoke("rollback-metadata-written");
        File.Delete(marker);
    }

    public static void UninstallBinaries(string home)
    {
        RequireAbsolute(home);
        using var lease = Lock(home);
        using var stateLease = StateLock(Path.Combine(home, "state"));
        var app = Path.Combine(home, "app");
        if (Directory.Exists(app)) MoveDirectory(app, Path.Combine(home, "app.uninstalled-" + Guid.NewGuid().ToString("N")));
    }

    public static void RecoverBeforeStartup(string home)
    {
        RequireAbsolute(home);
        using var lease = Lock(home);
        using var stateLease = StateLock(Path.Combine(home, "state"));
        var marker = Path.Combine(home, "run", "upgrade-in-progress.json");
        if (File.Exists(marker))
        {
            using var operation = JsonDocument.Parse(File.ReadAllText(marker));
            if (!operation.RootElement.TryGetProperty("phase", out var phase))
                throw new DeploymentException(6, "This interruption requires restore-based recovery; it cannot be recovered as a pre-start binary switch.");
            if (phase.GetString() == "rollback")
            {
                ResumeInterruptedRollback(home, operation.RootElement);
                return;
            }
            if (phase.GetString() is not ("staging" or "switching"))
                throw new DeploymentException(6, "This interruption requires restore-based recovery; it cannot be recovered as a pre-start binary switch.");
        }
        else
        {
            var record = Path.Combine(home, "run", "last-upgrade.json");
            if (!File.Exists(record)) throw new DeploymentException(6, "No recoverable upgrade evidence exists.");
            using var operation = JsonDocument.Parse(File.ReadAllText(record));
            if (!operation.RootElement.TryGetProperty("newHostStarted", out var started) || started.ValueKind != JsonValueKind.False)
                throw new DeploymentException(6, "A new host may have migrated state; use restore-backed rollback.");
        }
        // This command is only valid before any new host was started. It does not undo migrations.
        var old = Path.Combine(home, "app.old"); var app = Path.Combine(home, "app");
        if (Directory.Exists(old))
        {
            _ = ArtifactManifest.Verify(old);
            if (Directory.Exists(app)) MoveDirectory(app, Path.Combine(home, "app.interrupted-" + Guid.NewGuid().ToString("N")));
            MoveDirectory(old, app);
        }
        _ = ArtifactManifest.Verify(app);
        var staged = Path.Combine(home, "app.new");
        if (Directory.Exists(staged)) MoveDirectory(staged, Path.Combine(home, "app.staged-" + Guid.NewGuid().ToString("N")));
        InstallationMetadata.Write(home);
        File.Delete(marker);
    }

    private static FileStream StateLock(string state)
    {
        try { return new FileStream(state + ".maintenance.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DeploymentException(6, "Phase 17 maintenance lock is held; stop all writers before deployment."); }
    }

    private static void MoveDirectory(string source, string destination)
    {
        // Exclusive installation/state leases remain held while Windows scanners release handles.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (IOException) when (OperatingSystem.IsWindows() && attempt < 25) { Thread.Sleep(200); }
        }
    }

    private static void RequireAbsolute(string path) => ProductionConfiguration.Require(Path.IsPathFullyQualified(path), "path", "deployment paths must be absolute");
    private static void RejectOverlap(string source, string destination)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (source.Equals(destination, comparison) || source.StartsWith(destination + Path.DirectorySeparatorChar, comparison) || destination.StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw new DeploymentException(6, "Artifact source and installation home must not overlap.");
    }
    private static void RequirePlatform(ArtifactManifest manifest)
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var hostRid = OperatingSystem.IsWindows() && architecture == System.Runtime.InteropServices.Architecture.X64 ? "win-x64"
            : OperatingSystem.IsLinux() && architecture == System.Runtime.InteropServices.Architecture.X64 ? "linux-x64"
            : OperatingSystem.IsLinux() && architecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "unsupported";
        if (manifest.TargetRid != hostRid || (manifest.DistributionType != "platform-only" && hostRid == "linux-arm64"))
            throw new DeploymentException(5, "Artifact RID does not match this host; complete distributions support x64 and Platform-only also supports Linux ARM64.");
    }

    private static void ResumeInterruptedRollback(string home, JsonElement operation)
    {
        static string Child(JsonElement root, string property, string prefix, string home)
        {
            if (!root.TryGetProperty(property, out var item) || item.GetString() is not { } name || name != Path.GetFileName(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
                throw new DeploymentException(6, "Rollback recovery marker contains an invalid path; preserve all installation directories for manual recovery.");
            return Path.Combine(home, name);
        }
        var state = Path.Combine(home, "state");
        var restored = Child(operation, "restored", "state.restored-", home);
        _ = Child(operation, "failedState", "state.failed-", home);
        var failedApp = Child(operation, "failedApp", "app.failed-", home);
        var old = Path.Combine(home, "app.old");
        var app = Path.Combine(home, "app");
        if (!Directory.Exists(state))
        {
            if (!Directory.Exists(restored)) throw new DeploymentException(6, "Rollback recovery cannot find current or restored state; preserve all directories for manual recovery.");
            MoveDirectory(restored, state);
        }
        else if (Directory.Exists(restored))
        {
            // No state switch happened; state is still current and restored is a duplicate from the verified bundle.
            Directory.Delete(restored, recursive: true);
        }
        if (Directory.Exists(old))
        {
            if (Directory.Exists(app) && !Directory.Exists(failedApp)) MoveDirectory(app, failedApp);
            if (!Directory.Exists(app)) MoveDirectory(old, app);
        }
        else if (!Directory.Exists(app))
        {
            throw new DeploymentException(6, "Rollback recovery cannot find either installed or retained application binaries.");
        }
        _ = ArtifactManifest.Verify(app);
        InstallationMetadata.Write(home);
        File.Delete(Path.Combine(home, "run", "upgrade-in-progress.json"));
    }

    private static void CopyTree(string source, string destination, Action<string>? afterFileCopied = null)
    {
        if (Directory.Exists(destination)) throw new DeploymentException(6, "Staging directory already exists; inspect interrupted deployment.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var output = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, File.GetUnixFileMode(file));
            afterFileCopied?.Invoke(output);
        }
    }

    private static async Task BackupCommandAsync(string app, string[] arguments, CancellationToken token)
    {
        var manifest = ArtifactManifest.Verify(app);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = app };
        start.ArgumentList.Add(Path.Combine(app, "server", manifest.DistributionType == "platform-only" ? "Hispania.Platform.Server.dll" : "Hispania.Server.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Home overrides are irrelevant to the existing offline Phase 17 command.
        start.Environment.Remove("HISPANIA__HOME");
        using var process = Process.Start(start) ?? throw new DeploymentException(5, "The .NET runtime could not start the backup host.");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new DeploymentException(4, "Phase 17 backup/restore failed; preserve the backup and interruption marker and check offline backup prerequisites.");
    }
}
