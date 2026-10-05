using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Hispania.Inventory.Tests;

public sealed partial class ServerProcessRestartTests
{
    private readonly ITestOutputHelper disasterOutput;
    public ServerProcessRestartTests(ITestOutputHelper output) => disasterOutput = output;
    [Fact]
    [Trait("Category", "Phase17BackupRestore")]
    public async Task Phase17RealHostDestroyRestoreRetainsLoginBootstrapAssetsInventoryAndScheduledJob()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "hispania-disaster-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(testRoot, "host-a"); var target = Path.Combine(testRoot, "host-b");
        var bundle = Path.Combine(testRoot, "backup"); var library = Path.Combine(testRoot, "library"); Directory.CreateDirectory(library);
        var output = FindServerOutputDirectory(); var dll = Path.Combine(output, "Hispania.Server.dll");
        var admin = Guid.NewGuid().ToString("N"); var bootstrap = Guid.NewGuid().ToString("N"); var shutdown = Guid.NewGuid().ToString("N");
        var port = ReserveLoopbackPort();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        Process? server = null;
        try
        {
            server = StartManagedServer(dll, output, source, library, admin, bootstrap, shutdown, port);
            await WaitUntilHealthyAsync(server, client);
            var session = await CreateAndLoginIdentityUserAsync(client, admin);
            using var me = await GetJsonAsync(client, "/api/identity/me", session);
            var userId = ReadWrappedId(me.RootElement.GetProperty("user").GetProperty("id"));
            await AssertAuthorizationApiAsync(client, session, admin, bootstrap, source);
            var original = await AssertPersistentStateAsync(client, session, admin, 12, 5);
            await AssertAdminWritesAndAssociationAsync(client, admin, original.ItemId, output);
            original = await AssertPersistentStateAsync(client, session, admin, 12, 8);
            var job = await CreateClosureScheduledJobAsync(client, session);
            Assert.Equal(1, await BackupCommandAsync(dll, "--backup-create", source, bundle)); // Running host exclusion.
            await StopServerGracefullyAsync(server, client, shutdown); server.Dispose(); server = null;
            var semantic = Phase17BackupRestoreTests.Snapshot(source);
            var timer = Stopwatch.StartNew();
            Assert.Equal(0, await BackupCommandAsync(dll, "--backup-create", source, bundle));
            disasterOutput.WriteLine($"backupMs={timer.Elapsed.TotalMilliseconds:F0}"); timer.Restart();
            Assert.Equal(0, await BackupCommandAsync(dll, "--backup-verify", bundle));
            disasterOutput.WriteLine($"verifyMs={timer.Elapsed.TotalMilliseconds:F0}");
            SqliteConnection.ClearAllPools(); Directory.Delete(source, true);
            Assert.False(Directory.Exists(source)); Assert.False(Directory.Exists(target));
            timer.Restart(); Assert.Equal(0, await BackupCommandAsync(dll, "--backup-restore", bundle, target));
            disasterOutput.WriteLine($"restoreMs={timer.Elapsed.TotalMilliseconds:F0}");
            Assert.Equal(semantic, Phase17BackupRestoreTests.Snapshot(target));
            timer.Restart(); server = StartManagedServer(dll, output, target, library, admin, bootstrap, shutdown, port);
            await WaitUntilHealthyAsync(server, client);
            var readinessDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (true)
            {
                using var ready = await client.GetAsync("/readyz"); if (ready.IsSuccessStatusCode) break;
                Assert.True(DateTimeOffset.UtcNow < readinessDeadline, "Restored host must become Ready."); await Task.Delay(100);
            }
            disasterOutput.WriteLine($"startupReadyMs={timer.Elapsed.TotalMilliseconds:F0}");
            await AssertIdentitySessionAsync(client, session, HttpStatusCode.OK);
            Assert.NotNull(await LoginRestartUserForTokenAsync(client));
            var recovered = await AssertPersistentStateAsync(client, session, admin, 12, 8);
            Assert.Equal(original.ItemId, recovered.ItemId); Assert.Equal(original.AssetIds.Order(), recovered.AssetIds.Order());
            using (var pending = await WaitForJobStatusAsync(client, session, job, "Pending", TimeSpan.FromSeconds(10))) { }
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", bootstrap);
                using var response = await client.SendAsync(request); Assert.False(response.IsSuccessStatusCode);
            }
            await IssueOneUnitAsync(client, admin, recovered.ItemId, recovered.LocationId);
            await AssertPersistentStateAsync(client, session, admin, 11, 9);
            await StopServerGracefullyAsync(server, client, shutdown); server.Dispose(); server = null;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(); }
            server?.Dispose(); SqliteConnection.ClearAllPools(); if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }

    private static Process StartManagedServer(string dll, string output, string root, string library, string admin, string bootstrap, string shutdown, int port)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = output };
        foreach (var argument in new[] { dll, $"--urls=http://127.0.0.1:{port}", $"--Backup:ManagedRoot={root}", "--Library:Roots:0:Key=demo", $"--Library:Roots:0:Path={library}", "--Ffprobe:ExecutablePath=ffprobe", "--Ffmpeg:ExecutablePath=ffmpeg", $"--Modules:AdminToken={admin}", $"--Testing:ShutdownToken={shutdown}", "--Platform:Assets:MaximumBytes=1048576" }) start.ArgumentList.Add(argument);
        start.Environment["Identity__Bootstrap__Token"] = bootstrap;
        var process = Process.Start(start)!; _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync(); return process;
    }
    private static async Task<int> BackupCommandAsync(string dll, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); await output; await error; return process.ExitCode;
    }
}
