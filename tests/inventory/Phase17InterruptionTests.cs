using System.Diagnostics;
using Hispania.Server;
using Xunit;

namespace Hispania.Inventory.Tests;

[Trait("Category", "Phase17BackupRestore")]
public sealed class Phase17InterruptionTests
{
    [Theory]
    [InlineData("sqlite-capture")]
    [InlineData("storage-copy")]
    [InlineData("before-manifest")]
    [InlineData("before-publication")]
    [InlineData("backup-published")]
    public async Task KilledBackupLeavesSourceValidAndOnlyPublishesCompleteBundle(string checkpoint)
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); var before = Phase17BackupRestoreTests.Snapshot(fixture.Source);
        await KillAtAsync(checkpoint, "--backup-create", fixture.Source, fixture.Bundle);
        Assert.Equal(before, Phase17BackupRestoreTests.Snapshot(fixture.Source));
        using (BackupInstallation.Lock(fixture.Source)) { }
        if (checkpoint == "backup-published") await BackupInstallation.VerifyAsync(fixture.Bundle, default);
        else Assert.False(Directory.Exists(fixture.Bundle));
    }
    [Theory]
    [InlineData("restore-staging")]
    [InlineData("restore-before-switch")]
    [InlineData("restore-switch")]
    [InlineData("restore-published")]
    public async Task KilledRestoreLeavesAbsentTargetAndNeverPermitsPartialStartup(string checkpoint)
    {
        using var fixture = new Phase17BackupRestoreTests.Installation();
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        await KillAtAsync(checkpoint, "--backup-restore", fixture.Bundle, fixture.Target);
        Assert.Equal(checkpoint == "restore-published", Directory.Exists(fixture.Target));
        Assert.Equal(checkpoint is "restore-switch" or "restore-published", File.Exists(fixture.Target + ".restore-in-progress"));
        if (checkpoint == "restore-published") Assert.Equal(Phase17BackupRestoreTests.Snapshot(fixture.Source), Phase17BackupRestoreTests.Snapshot(fixture.Target));
        await BackupInstallation.VerifyAsync(fixture.Bundle, default);
    }
    private static async Task KillAtAsync(string checkpoint, params string[] arguments)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Hispania.sln"))) repo = repo.Parent;
        var dll = Path.Combine(repo!.FullName, "tests", "backup-process-host", "bin", "Debug", "net10.0", "Hispania.Backup.ProcessHost.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll); start.ArgumentList.Add(checkpoint); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!; var error = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30); var paused = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (line is null) break;
                if (line == "checkpoint:" + checkpoint) { paused = true; break; }
            }
            Assert.True(paused, "Child must reach the exact interruption checkpoint.");
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); await error; }
    }
}
