using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed partial class ServerProcessRestartTests
{
    [Fact]
    public async Task PlatformAdminProjectionsRequireSessionPermissionsAndExcludePrivateMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-admin-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var serverDirectory = FindServerOutputDirectory(); var legacyToken = Guid.NewGuid().ToString("N");
        var bootstrapToken = Guid.NewGuid().ToString("N"); var shutdownToken = Guid.NewGuid().ToString("N");
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ReserveLoopbackPort()}"), Timeout = TimeSpan.FromSeconds(15) };
        Process? server = null;
        try
        {
            server = StartServer(Path.Combine(serverDirectory, "Hispania.Server.dll"), serverDirectory, root, library, legacyToken, bootstrapToken, shutdownToken, client.BaseAddress.Port);
            await WaitUntilHealthyAsync(server, client);
            var session = await CreateAndLoginIdentityUserAsync(client, legacyToken);
            var paths = new[] { "/api/platform/admin/overview", "/api/platform/admin/modules", $"/api/platform/admin/assets/{Guid.NewGuid():D}" };
            foreach (var path in paths)
            {
                using var anonymous = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                using var bearer = new HttpRequestMessage(HttpMethod.Get, path); bearer.Headers.Authorization = new("Bearer", legacyToken);
                using var rejected = await client.SendAsync(bearer); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                using var forbidden = await SendWithSessionAsync(client, HttpMethod.Get, path, session); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
            using var me = await SendWithSessionAsync(client, HttpMethod.Get, "/api/identity/me", session);
            using var current = JsonDocument.Parse(await me.Content.ReadAsStringAsync()); var userId = current.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid();
            using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) };
            bootstrap.Headers.Authorization = new("Bootstrap", bootstrapToken); using var granted = await client.SendAsync(bootstrap); granted.EnsureSuccessStatusCode();

            async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? token = null)
            {
                using var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new("Session", token ?? session);
                request.Headers.Add("X-Correlation-ID", "admin-test-flow");
                if (body is not null) request.Content = JsonContent.Create(body);
                return await client.SendAsync(request);
            }
            using var overview = await SendAsync(HttpMethod.Get, paths[0]); overview.EnsureSuccessStatusCode();
            var overviewText = await overview.Content.ReadAsStringAsync(); using var overviewJson = JsonDocument.Parse(overviewText);
            Assert.Equal(1, overviewJson.RootElement.GetProperty("activeUsers").GetInt32());
            Assert.True(overviewJson.RootElement.GetProperty("activeSessions").GetDouble() >= 1);
            Assert.Equal("Healthy", overviewJson.RootElement.GetProperty("storageStatus").GetString());
            Assert.True(overviewJson.RootElement.GetProperty("modules").GetProperty("total").GetInt32() > 0);

            // Fault injection stays in test fixtures; the production projection only uses contracts.
            foreach (var subsystem in new[] { "Events", "Jobs" })
            {
                async Task RenameStoreTableAsync(bool unavailable)
                {
                    if (subsystem == "Events") { await RenameDeliveryTableAsync(root, unavailable); return; }
                    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "platform", "jobs.sqlite") }.ToString());
                    await connection.OpenAsync(); await using var command = connection.CreateCommand();
                    command.CommandText = unavailable ? "ALTER TABLE platform_jobs RENAME TO unavailable_jobs" : "ALTER TABLE unavailable_jobs RENAME TO platform_jobs";
                    await command.ExecuteNonQueryAsync();
                }
                await RenameStoreTableAsync(true);
                try
                {
                    using (var live = await client.GetAsync("/api/health/live")) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                    using (var ready = await client.GetAsync("/api/health/ready"))
                    {
                        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                        Assert.DoesNotContain("sqlite", await ready.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
                    }
                    using var degraded = await SendAsync(HttpMethod.Get, paths[0]); degraded.EnsureSuccessStatusCode();
                    var degradedText = await degraded.Content.ReadAsStringAsync(); using var degradedJson = JsonDocument.Parse(degradedText);
                    Assert.Equal("Unhealthy", degradedJson.RootElement.GetProperty("status").GetString());
                    Assert.Equal(JsonValueKind.Null, degradedJson.RootElement.GetProperty(subsystem == "Jobs" ? "pendingJobs" : "deadLetterEvents").ValueKind);
                    Assert.Equal("Healthy", degradedJson.RootElement.GetProperty("storageStatus").GetString());
                    Assert.DoesNotContain("SqliteException", degradedText, StringComparison.Ordinal); Assert.DoesNotContain(root, degradedText, StringComparison.Ordinal);
                }
                finally { await RenameStoreTableAsync(false); }
                using var recovered = await SendAsync(HttpMethod.Get, paths[0]); recovered.EnsureSuccessStatusCode();
                using var recoveredJson = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync()); Assert.Equal("Healthy", recoveredJson.RootElement.GetProperty("status").GetString());
            }

            using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new StringContent("private-content") };
            upload.Headers.Authorization = new("Session", session); upload.Content.Headers.ContentType = new("text/plain");
            upload.Headers.Add("X-Asset-Type", "Document"); upload.Headers.Add("X-Asset-Domain", "private-domain"); upload.Headers.Add("X-Asset-Owner", "private-owner");
            using var uploaded = await client.SendAsync(upload); uploaded.EnsureSuccessStatusCode(); using var createdAsset = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            var assetId = createdAsset.RootElement.GetProperty("id").GetProperty("value").GetGuid();
            using var asset = await SendAsync(HttpMethod.Get, $"/api/platform/admin/assets/{assetId:D}"); asset.EnsureSuccessStatusCode();
            var assetText = await asset.Content.ReadAsStringAsync(); using var assetJson = JsonDocument.Parse(assetText);
            Assert.Equal("text/plain", assetJson.RootElement.GetProperty("mimeType").GetString()); Assert.Equal("filesystem", assetJson.RootElement.GetProperty("provider").GetString());
            Assert.False(assetJson.RootElement.TryGetProperty("contentHandle", out _)); Assert.DoesNotContain("private-owner", assetText, StringComparison.Ordinal);
            using var missing = await SendAsync(HttpMethod.Get, paths[2]); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var modules = await SendAsync(HttpMethod.Get, paths[1]); modules.EnsureSuccessStatusCode(); var moduleText = await modules.Content.ReadAsStringAsync();
            using var moduleJson = JsonDocument.Parse(moduleText); Assert.NotEmpty(moduleJson.RootElement.EnumerateArray());
            foreach (var row in moduleJson.RootElement.EnumerateArray())
            { Assert.True(row.TryGetProperty("executionModel", out _)); Assert.True(row.TryGetProperty("generation", out _)); Assert.False(row.TryGetProperty("detail", out _)); Assert.False(row.TryGetProperty("manifestPath", out _)); }
            using var unknownOperation = await SendAsync(HttpMethod.Post, "/api/platform/admin/modules/unknown/activate"); Assert.Equal(HttpStatusCode.NotFound, unknownOperation.StatusCode);
            using var invalidVersion = await SendAsync(HttpMethod.Post, "/api/platform/admin/modules/unknown/update?version=invalid"); Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
            using var audit = await SendAsync(HttpMethod.Get, "/internal/admin/audit?domain=platform.modules&correlationId=admin-test-flow"); audit.EnsureSuccessStatusCode();
            var auditText = await audit.Content.ReadAsStringAsync(); Assert.Contains("activate", auditText, StringComparison.Ordinal); Assert.Contains("failure", auditText, StringComparison.Ordinal); Assert.Contains(userId.ToString("D"), auditText, StringComparison.Ordinal);

            // An observability-only operator sees aggregates, but no identity/module summary or module actions.
            using var createdUser = await SendAsync(HttpMethod.Post, "/api/identity/users", new { username = "Observer", displayName = "Observer", password = "another persistent test passphrase" }); createdUser.EnsureSuccessStatusCode();
            using var observerUser = JsonDocument.Parse(await createdUser.Content.ReadAsStringAsync()); var observerId = observerUser.RootElement.GetProperty("id").GetProperty("value").GetGuid();
            using var role = await SendAsync(HttpMethod.Post, "/api/authorization/roles", new { name = "observer", description = "Observability only" }); role.EnsureSuccessStatusCode();
            using var roleJson = JsonDocument.Parse(await role.Content.ReadAsStringAsync()); var roleId = roleJson.RootElement.GetProperty("id").GetProperty("value").GetGuid();
            using var permission = await SendAsync(HttpMethod.Put, $"/api/authorization/roles/{roleId:D}/permissions/platform.observability.read"); permission.EnsureSuccessStatusCode();
            using var assignment = await SendAsync(HttpMethod.Post, $"/api/authorization/users/{observerId:D}/roles/{roleId:D}"); assignment.EnsureSuccessStatusCode();
            using var login = await client.PostAsJsonAsync("/api/identity/login", new { username = "Observer", password = "another persistent test passphrase" }); login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync()); var observerToken = loginJson.RootElement.GetProperty("sessionToken").GetString()!;
            using var partial = await SendAsync(HttpMethod.Get, paths[0], token: observerToken); partial.EnsureSuccessStatusCode(); using var partialJson = JsonDocument.Parse(await partial.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, partialJson.RootElement.GetProperty("activeUsers").ValueKind); Assert.Equal(JsonValueKind.Null, partialJson.RootElement.GetProperty("modules").ValueKind);
            using var deniedAction = await SendAsync(HttpMethod.Post, "/api/platform/admin/modules/unknown/activate", token: observerToken); Assert.Equal(HttpStatusCode.Forbidden, deniedAction.StatusCode);
            using var scheduled = await SendAsync(HttpMethod.Post, "/api/platform/jobs", new { jobType = "platform.test.hash", payload = new { text = "private scheduled job", algorithm = "sha256" }, scheduledAt = DateTimeOffset.UtcNow.AddHours(1) }); scheduled.EnsureSuccessStatusCode();
            using var scheduledJson = JsonDocument.Parse(await scheduled.Content.ReadAsStringAsync()); var jobId = scheduledJson.RootElement.GetProperty("jobId").GetGuid();
            foreach (var area in new[] { "jobs", "events", "users" })
            {
                var username = "Read" + area;
                using var readerUserResponse = await SendAsync(HttpMethod.Post, "/api/identity/users", new { username, displayName = username, password = "isolated read-only test passphrase" }); readerUserResponse.EnsureSuccessStatusCode();
                using var readerUserJson = JsonDocument.Parse(await readerUserResponse.Content.ReadAsStringAsync()); var readerId = readerUserJson.RootElement.GetProperty("id").GetProperty("value").GetGuid();
                using var readerRoleResponse = await SendAsync(HttpMethod.Post, "/api/authorization/roles", new { name = "read-" + area, description = "Isolated read-only role" }); readerRoleResponse.EnsureSuccessStatusCode();
                using var readerRoleJson = JsonDocument.Parse(await readerRoleResponse.Content.ReadAsStringAsync()); var readerRoleId = readerRoleJson.RootElement.GetProperty("id").GetProperty("value").GetGuid();
                using var readPermission = await SendAsync(HttpMethod.Put, $"/api/authorization/roles/{readerRoleId:D}/permissions/platform.{area}.read"); readPermission.EnsureSuccessStatusCode();
                using var readAssignment = await SendAsync(HttpMethod.Post, $"/api/authorization/users/{readerId:D}/roles/{readerRoleId:D}"); readAssignment.EnsureSuccessStatusCode();
                using var readerLogin = await client.PostAsJsonAsync("/api/identity/login", new { username, password = "isolated read-only test passphrase" }); readerLogin.EnsureSuccessStatusCode();
                using var readerLoginJson = JsonDocument.Parse(await readerLogin.Content.ReadAsStringAsync()); var readerToken = readerLoginJson.RootElement.GetProperty("sessionToken").GetString()!;
                var readPath = area == "jobs" ? "/api/platform/jobs" : area == "events" ? "/internal/admin/events/deliveries" : "/api/authorization/users";
                using var read = await SendAsync(HttpMethod.Get, readPath, token: readerToken); read.EnsureSuccessStatusCode();
                var readText = await read.Content.ReadAsStringAsync(); Assert.DoesNotContain("payloadJson", readText, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("passwordHash", readText, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(readerToken, readText, StringComparison.Ordinal);
                if (area == "jobs")
                {
                    foreach (var suffix in new[] { "", "/attempts" }) { using var detail = await SendAsync(HttpMethod.Get, $"/api/platform/jobs/{jobId:D}{suffix}", token: readerToken); detail.EnsureSuccessStatusCode(); }
                    foreach (var operation in new[] { "cancel", "retry" }) { using var denied = await SendAsync(HttpMethod.Post, $"/api/platform/jobs/{jobId:D}/{operation}", token: readerToken); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
                }
                else if (area == "events")
                {
                    using var denied = await SendAsync(HttpMethod.Post, $"/internal/admin/events/deliveries/{Guid.NewGuid():D}/replay", token: readerToken); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                    // Raw payload/attempt APIs retain their pre-existing manage guard; read uses delivery metadata.
                    using var payloadDenied = await SendAsync(HttpMethod.Get, $"/internal/admin/events/{Guid.NewGuid():D}", token: readerToken); Assert.Equal(HttpStatusCode.Forbidden, payloadDenied.StatusCode);
                }
                else
                {
                    using var detail = await SendAsync(HttpMethod.Get, $"/api/authorization/users/{userId:D}/roles", token: readerToken); detail.EnsureSuccessStatusCode();
                    using var denied = await SendAsync(HttpMethod.Patch, $"/api/authorization/users/{observerId:D}/status", new { enabled = false }, readerToken); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                }
                using var moduleDenied = await SendAsync(HttpMethod.Get, paths[1], token: readerToken); Assert.Equal(HttpStatusCode.Forbidden, moduleDenied.StatusCode);
                using var overviewDenied = await SendAsync(HttpMethod.Get, paths[0], token: readerToken); Assert.Equal(HttpStatusCode.Forbidden, overviewDenied.StatusCode);
            }
            using var revoked = await SendAsync(HttpMethod.Post, "/api/identity/logout", token: observerToken); revoked.EnsureSuccessStatusCode();
            using var expired = await SendAsync(HttpMethod.Get, paths[0], token: observerToken); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            foreach (var text in new[] { overviewText, assetText, moduleText })
                foreach (var secret in new[] { root, legacyToken, bootstrapToken, session, "private-content", "connectionString", "passwordHash", "manifestPath" })
                    Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            await StopServerGracefullyAsync(server, client, shutdownToken); server.Dispose(); server = null;
        }
        finally
        {
            if (server is not null) { if (!server.HasExited) server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); server.Dispose(); }
            SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true);
        }
    }
}
