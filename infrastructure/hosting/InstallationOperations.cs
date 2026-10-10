using System.Diagnostics;
using System.Text.Json;

namespace Hispania.Hosting;

public static class InstallationOperations
{
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
    {
        RequireAbsolute(home); RequireAbsolute(artifact);
        RejectOverlap(artifact, home);
        RequirePlatform(ArtifactManifest.Verify(artifact));
        using var lease = Lock(home);
        var marker = Path.Combine(home, "run", "upgrade-in-progress.json");
        if (File.Exists(marker) || Directory.Exists(Path.Combine(home, "app.new")) || Directory.Exists(Path.Combine(home, "app.old"))) throw new DeploymentException(6, "An earlier upgrade requires explicit recovery; preserve app.old and backups.");
        var app = Path.Combine(home, "app");
        _ = ArtifactManifest.Verify(app);
        var state = Path.Combine(home, "state");
        using (StateLock(state)) { } // Stop verified before staging and backup.
        var bundle = Path.Combine(home, "backups", "pre-upgrade-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase = "staging", backup = bundle }, ProductionConfiguration.Json));
        CopyTree(artifact, Path.Combine(home, "app.new"));
        _ = ArtifactManifest.Verify(Path.Combine(home, "app.new"));
        await BackupCommandAsync(app, ["--backup-create", state, bundle], token);
        await BackupCommandAsync(app, ["--backup-verify", bundle], token);
        using var stateLease = StateLock(state);
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase = "switching", backup = bundle }, ProductionConfiguration.Json));
        MoveDirectory(app, Path.Combine(home, "app.old"));
        MoveDirectory(Path.Combine(home, "app.new"), app);
        InstallationMetadata.Write(home);
        File.WriteAllText(Path.Combine(home, "run", "last-upgrade.json"), JsonSerializer.Serialize(new { backup = bundle, newHostStarted = false, migrations = "unknown after the new host starts; restore is required for rollback after startup" }, ProductionConfiguration.Json));
        File.Delete(marker);
        return bundle;
    }

    public static async Task RestoreRollbackAsync(string home, string bundle, CancellationToken token = default)
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
        using var stateLease = StateLock(state);
        var marker = Path.Combine(home, "run", "upgrade-in-progress.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase = "rollback", backup = bundle }, ProductionConfiguration.Json));
        MoveDirectory(state, Path.Combine(home, "state.failed-" + Guid.NewGuid().ToString("N")));
        MoveDirectory(restored, state);
        var app = Path.Combine(home, "app");
        if (Directory.Exists(app)) MoveDirectory(app, Path.Combine(home, "app.failed-" + Guid.NewGuid().ToString("N")));
        MoveDirectory(old, app);
        InstallationMetadata.Write(home);
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
            if (!operation.RootElement.TryGetProperty("phase", out var phase) || phase.GetString() is not ("staging" or "switching"))
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
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || manifest.TargetRid != (OperatingSystem.IsWindows() ? "win-x64" : "linux-x64") || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new DeploymentException(5, "Artifact RID does not match the supported x64 host.");
    }

    private static void CopyTree(string source, string destination)
    {
        if (Directory.Exists(destination)) throw new DeploymentException(6, "Staging directory already exists; inspect interrupted deployment.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var output = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, File.GetUnixFileMode(file));
        }
    }

    private static async Task BackupCommandAsync(string app, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = app };
        start.ArgumentList.Add(Path.Combine(app, "server", "Hispania.Server.dll"));
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
