using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed partial class ServerProcessRestartTests
{
    [Fact]
    public async Task ObservabilityHttpCorrelationAuditJobsAndDetailRbacAreConsistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-observability-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var serverDirectory = FindServerOutputDirectory(); var adminToken = Guid.NewGuid().ToString("N"); var bootstrapToken = Guid.NewGuid().ToString("N"); var shutdownToken = Guid.NewGuid().ToString("N");
        var port = ReserveLoopbackPort(); using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) }; Process? server = null;
        try
        {
            server = StartServer(Path.Combine(serverDirectory, "Hispania.Server.dll"), serverDirectory, root, library, adminToken, bootstrapToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            using var live = await client.GetAsync("/api/health/live"); live.EnsureSuccessStatusCode();
            Assert.Single(live.Headers.GetValues("X-Correlation-ID"));
            using var custom = new HttpRequestMessage(HttpMethod.Get, "/api/health"); custom.Headers.Add("X-Correlation-ID", "client-flow-A");
            using var customResponse = await client.SendAsync(custom); Assert.Equal("client-flow-A", Assert.Single(customResponse.Headers.GetValues("X-Correlation-ID")));
            using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/health"); invalid.Headers.Add("X-Correlation-ID", new string('x', 129));
            using var invalidResponse = await client.SendAsync(invalid); Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            using var anonymous = await client.GetAsync("/api/health/details"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var legacy = new HttpRequestMessage(HttpMethod.Get, "/api/platform/observability"); legacy.Headers.Authorization = new("Bearer", adminToken);
            using var rejected = await client.SendAsync(legacy); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            var session = await CreateAndLoginIdentityUserAsync(client, adminToken);
            using var forbidden = await SendWithSessionAsync(client, HttpMethod.Get, "/api/platform/observability", session); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var me = await SendWithSessionAsync(client, HttpMethod.Get, "/api/identity/me", session); using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            var userId = meJson.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid();
            using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) }; bootstrap.Headers.Authorization = new("Bootstrap", bootstrapToken);
            using var bootstrapped = await client.SendAsync(bootstrap); bootstrapped.EnsureSuccessStatusCode();
            using var jobRequest = new HttpRequestMessage(HttpMethod.Post, "/api/platform/jobs") { Content = JsonContent.Create(new { jobType = "platform.test.hash", payload = new { text = "private-payload", algorithm = "sha256" }, scheduledAt = DateTimeOffset.UtcNow.AddHours(1) }) };
            jobRequest.Headers.Authorization = new("Session", session); jobRequest.Headers.Add("X-Correlation-ID", "job-flow-A");
            using var created = await client.SendAsync(jobRequest); Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            using var job = JsonDocument.Parse(await created.Content.ReadAsStringAsync()); Assert.Equal("job-flow-A", job.RootElement.GetProperty("correlationId").GetString());
            Assert.Equal("job-flow-A", Assert.Single(created.Headers.GetValues("X-Correlation-ID"))); Assert.False(string.IsNullOrEmpty(job.RootElement.GetProperty("causationId").GetString()));
            using var audit = await SendWithSessionAsync(client, HttpMethod.Get, "/internal/admin/audit?correlationId=job-flow-A", session); audit.EnsureSuccessStatusCode(); Assert.Contains("job-flow-A", await audit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var snapshot = await SendWithSessionAsync(client, HttpMethod.Get, "/api/platform/observability", session); snapshot.EnsureSuccessStatusCode();
            var json = await snapshot.Content.ReadAsStringAsync(); using var parsed = JsonDocument.Parse(json); Assert.Equal("Healthy", parsed.RootElement.GetProperty("health").GetProperty("status").GetString());
            Assert.DoesNotContain(session, json, StringComparison.Ordinal); Assert.DoesNotContain(bootstrapToken, json, StringComparison.Ordinal); Assert.DoesNotContain(root, json, StringComparison.Ordinal); Assert.DoesNotContain("private-payload", json, StringComparison.Ordinal);
            Assert.Contains("hispania.identity.sessions.active", json, StringComparison.Ordinal);
            await StopServerGracefullyAsync(server, client, shutdownToken); server.Dispose(); server = null;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
            server?.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }
}
