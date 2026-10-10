using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed partial class ServerProcessRestartTests
{
    private static async Task<Guid> CreateClosureScheduledJobAsync(HttpClient client, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/jobs")
        {
            Content = JsonContent.Create(new { jobType = "platform.test.hash", payload = new { text = "closure", algorithm = "sha256" }, scheduledAt = DateTimeOffset.UtcNow.AddHours(1) })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Session", session);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("jobId").GetGuid();
    }

    private static async Task CancelClosureScheduledJobAsync(HttpClient client, string session, Guid id)
    {
        foreach (var operation in new[] { "cancel" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/jobs/{id:D}/{operation}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Session", session);
            using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        }
        using var completed = await WaitForJobStatusAsync(client, session, id, "Cancelled", TimeSpan.FromSeconds(30));
    }

    private static async Task<Guid[]> ReadClosureEventIdsAsync(HttpClient client, string session)
    {
        using var response = await SendWithSessionAsync(client, HttpMethod.Get, "/internal/admin/events/deliveries?limit=200", session);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.EnumerateArray().Select(row => row.GetProperty("eventId").GetGuid()).Distinct().Order().ToArray();
        Assert.NotEmpty(ids);
        return ids;
    }

    private static async Task AssertClosureHealthAsync(HttpClient client, string session)
    {
        foreach (var path in new[] { "/api/health/live", "/api/health/ready" })
        {
            using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
        }
        using var detail = await SendWithSessionAsync(client, HttpMethod.Get, "/api/platform/observability", session);
        detail.EnsureSuccessStatusCode();
        var body = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sqlite", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealCrashRecoversRunningJobAndProcessingEventTogetherWithoutSharingStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-closure-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? victim = null;
        Process? server = null;
        try
        {
            var platform = Path.Combine(root, "platform"); Directory.CreateDirectory(platform);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var fixture = Path.Combine(FindRepositoryRoot(), "tests", "closure-process-host", "bin", configuration, "net10.0", "Hispania.Closure.ProcessHost.dll");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(fixture); start.ArgumentList.Add(platform);
            victim = Process.Start(start)!;
            var error = victim.StandardError.ReadToEndAsync();
            var ready = await victim.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(string.IsNullOrWhiteSpace(ready), "Crash fixture must acknowledge both committed leases.");
            using var ids = JsonDocument.Parse(ready!);
            var jobId = ids.RootElement.GetProperty("jobId").GetGuid();
            var eventId = ids.RootElement.GetProperty("eventId").GetGuid();
            var jobsPath = Path.Combine(platform, "jobs.sqlite"); var eventsPath = Path.Combine(platform, "events.sqlite");
            var jobs = new SqlitePlatformJobStore(jobsPath);
            Assert.Equal(PlatformJobStatusV1.Running, (await jobs.GetAsync(jobId, default))!.Status);
            using (var events = new SqliteDurablePlatformEventBus(eventsPath))
                Assert.Equal(DurableDeliveryStatus.Processing, Assert.Single((await events.GetEventAsync(eventId))!.Deliveries).Status);
            victim.Kill(entireProcessTree: true);
            await victim.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(await error); victim.Dispose(); victim = null;

            // Recover the crashed executor's durable state using the production server, not a mock store.
            var library = Path.Combine(root, "empty-library"); Directory.CreateDirectory(library);
            var directory = FindServerOutputDirectory(); var port = ReserveLoopbackPort();
            var admin = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            var bootstrap = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            var shutdown = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
            server = StartServer(Path.Combine(directory, "Hispania.Server.dll"), directory, root, library, admin, bootstrap, shutdown, port);
            await WaitUntilHealthyAsync(server, client);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                using var events = new SqliteDurablePlatformEventBus(eventsPath);
                var delivery = Assert.Single((await events.GetEventAsync(eventId))!.Deliveries);
                if ((await jobs.GetAsync(jobId, default))!.Status == PlatformJobStatusV1.Succeeded && delivery.Status == DurableDeliveryStatus.Delivered) break;
                await Task.Delay(100);
            }
            Assert.Equal(PlatformJobStatusV1.Succeeded, (await jobs.GetAsync(jobId, default))!.Status);
            var attempts = await jobs.GetAttemptsAsync(jobId, default);
            Assert.Collection(attempts, attempt => Assert.Equal(PlatformJobAttemptOutcomeV1.Recovered, attempt.Outcome), attempt => Assert.Equal(PlatformJobAttemptOutcomeV1.Succeeded, attempt.Outcome));
            Assert.False(await jobs.CompleteAsync(jobId, 1, "{\"stale\":true}", default));
            using (var events = new SqliteDurablePlatformEventBus(eventsPath))
            {
                var delivery = Assert.Single((await events.GetEventAsync(eventId))!.Deliveries);
                Assert.Equal(DurableDeliveryStatus.Delivered, delivery.Status); Assert.Equal(2, delivery.AttemptCount);
            }
            foreach (var path in new[] { jobsPath, eventsPath })
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", command.ExecuteScalar());
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = command.ExecuteReader(); var tables = new List<string>(); while (reader.Read()) tables.Add(reader.GetString(0));
                if (path == jobsPath) Assert.DoesNotContain("Events", tables); else Assert.DoesNotContain("platform_jobs", tables);
            }
            await StopServerGracefullyAsync(server, client, shutdown); server.Dispose(); server = null;
        }
        finally
        {
            foreach (var process in new[] { victim, server })
            {
                if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true);
        }
    }
}
