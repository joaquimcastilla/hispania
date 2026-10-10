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
    public async Task Phase22MaintenanceHttpRoutesEnforceGranularRbacAuditAndSafeExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase22-maintenance-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var serverDirectory = FindServerOutputDirectory();
        var legacyToken = Guid.NewGuid().ToString("N");
        var bootstrapToken = Guid.NewGuid().ToString("N");
        var shutdownToken = Guid.NewGuid().ToString("N");
        var port = ReserveLoopbackPort();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(15) };
        Process? server = null;
        try
        {
            server = StartServer(Path.Combine(serverDirectory, "Hispania.Server.dll"), serverDirectory,
                root, library, legacyToken, bootstrapToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);

            const string ownersPath = "/api/platform/maintenance/owners";
            const string capacityPath = "/api/platform/maintenance/capacity";
            const string plansPath = "/api/platform/maintenance/retention/plans";
            using (var anonymous = await client.GetAsync(ownersPath)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var legacy = new HttpRequestMessage(HttpMethod.Get, ownersPath))
            {
                legacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
                using var denied = await client.SendAsync(legacy);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }

            var administratorToken = await CreateAndLoginIdentityUserAsync(client, legacyToken);
            using (var noPermission = await SendWithSessionAsync(client, HttpMethod.Get, ownersPath, administratorToken))
                Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
            using (var noPermission = await SendWithSessionAsync(client, HttpMethod.Get, capacityPath, administratorToken))
                Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);
            using (var noPermission = await SendWithSessionAsync(client, HttpMethod.Post, plansPath, administratorToken,
                new { ownerId = "platform.jobs", enabled = false, minimumAgeDays = 30, batchSize = 10 }))
                Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);

            using var me = await SendWithSessionAsync(client, HttpMethod.Get, "/api/identity/me", administratorToken);
            using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            var administratorId = meJson.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid();
            using (var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin")
            {
                Content = JsonContent.Create(new { userId = administratorId })
            })
            {
                bootstrap.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", bootstrapToken);
                using var granted = await client.SendAsync(bootstrap);
                Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
            }

            using var createUser = new HttpRequestMessage(HttpMethod.Post, "/api/identity/users")
            {
                Content = JsonContent.Create(new { username = "MaintenanceReader", displayName = "Maintenance Reader", password = "a separate maintenance test passphrase" })
            };
            createUser.Headers.Authorization = new AuthenticationHeaderValue("Session", administratorToken);
            using var createdUser = await client.SendAsync(createUser);
            Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);
            using var createdUserJson = JsonDocument.Parse(await createdUser.Content.ReadAsStringAsync());
            var readerId = createdUserJson.RootElement.GetProperty("id").GetProperty("value").GetGuid();

            using var createRole = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/roles")
            {
                Content = JsonContent.Create(new { name = "phase22-maintenance-reader", description = "Phase 22 isolated RBAC integration" })
            };
            createRole.Headers.Authorization = new AuthenticationHeaderValue("Session", administratorToken);
            using var createdRole = await client.SendAsync(createRole);
            Assert.Equal(HttpStatusCode.Created, createdRole.StatusCode);
            using var createdRoleJson = JsonDocument.Parse(await createdRole.Content.ReadAsStringAsync());
            var roleId = createdRoleJson.RootElement.GetProperty("id").GetProperty("value").GetGuid();

            await GrantPermissionAsync(client, administratorToken, roleId, "platform.capacity.read");
            using (var assignment = new HttpRequestMessage(HttpMethod.Post, $"/api/authorization/users/{readerId:D}/roles/{roleId:D}"))
            {
                assignment.Headers.Authorization = new AuthenticationHeaderValue("Session", administratorToken);
                using var assigned = await client.SendAsync(assignment);
                Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            }
            using var login = await client.PostAsJsonAsync("/api/identity/login", new { username = "MaintenanceReader", password = "a separate maintenance test passphrase" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var readerToken = Assert.IsType<string>(loginJson.RootElement.GetProperty("sessionToken").GetString());

            using (var capacity = await SendWithSessionAsync(client, HttpMethod.Get, capacityPath, readerToken))
                Assert.Equal(HttpStatusCode.OK, capacity.StatusCode);
            using (var ownersDenied = await SendWithSessionAsync(client, HttpMethod.Get, ownersPath, readerToken))
                Assert.Equal(HttpStatusCode.Forbidden, ownersDenied.StatusCode);
            using (var planDenied = await SendWithSessionAsync(client, HttpMethod.Post, plansPath, readerToken,
                new { ownerId = "platform.jobs", enabled = true, minimumAgeDays = 30, batchSize = 10 }))
                Assert.Equal(HttpStatusCode.Forbidden, planDenied.StatusCode);

            await GrantPermissionAsync(client, administratorToken, roleId, "platform.maintenance.read");
            using (var owners = await SendWithSessionAsync(client, HttpMethod.Get, ownersPath, readerToken))
                Assert.Equal(HttpStatusCode.OK, owners.StatusCode);
            using (var malformed = await SendWithSessionAsync(client, HttpMethod.Post, plansPath, readerToken,
                new { ownerId = "platform.jobs", enabled = true, minimumAgeDays = 30, batchSize = 0 }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
                Assert.Contains("invalid_retention_policy", await malformed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            var correlationId = "phase22-maintenance-http-test";
            using var planRequest = new HttpRequestMessage(HttpMethod.Post, plansPath)
            {
                Content = JsonContent.Create(new { ownerId = "platform.jobs", enabled = true, minimumAgeDays = 30, batchSize = 10 })
            };
            planRequest.Headers.Authorization = new AuthenticationHeaderValue("Session", readerToken);
            planRequest.Headers.Add("X-Correlation-ID", correlationId);
            using var planResponse = await client.SendAsync(planRequest);
            Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
            using var planJson = JsonDocument.Parse(await planResponse.Content.ReadAsStringAsync());
            Assert.Equal(0, planJson.RootElement.GetProperty("candidateCount").GetInt32());
            var planId = planJson.RootElement.GetProperty("planId").GetGuid();
            var executePath = $"/api/platform/maintenance/retention/plans/{planId:D}/execute";

            using (var executeDenied = await SendWithSessionAsync(client, HttpMethod.Post, executePath, readerToken,
                new { confirm = true, maximumBatches = 1 }))
                Assert.Equal(HttpStatusCode.Forbidden, executeDenied.StatusCode);
            await GrantPermissionAsync(client, administratorToken, roleId, "platform.maintenance.execute");
            using (var unconfirmed = await SendWithSessionAsync(client, HttpMethod.Post, executePath, readerToken,
                new { confirm = false, maximumBatches = 1 }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
                Assert.Contains("confirmation_or_batch_limit_required", await unconfirmed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var execute = new HttpRequestMessage(HttpMethod.Post, executePath)
            {
                Content = JsonContent.Create(new { confirm = true, maximumBatches = 1 })
            })
            {
                execute.Headers.Authorization = new AuthenticationHeaderValue("Session", readerToken);
                execute.Headers.Add("X-Correlation-ID", correlationId);
                using var executed = await client.SendAsync(execute);
                Assert.Equal(HttpStatusCode.OK, executed.StatusCode);
                using var runJson = JsonDocument.Parse(await executed.Content.ReadAsStringAsync());
                Assert.Equal(0, runJson.RootElement.GetProperty("affected").GetInt64());
                Assert.Equal((int)Hispania.Platform.Contracts.Maintenance.MaintenanceRunStateV1.Completed,
                    runJson.RootElement.GetProperty("state").GetInt32());
            }

            using (var audit = await SendWithSessionAsync(client, HttpMethod.Get,
                $"/internal/admin/audit?domain=platform.maintenance&correlationId={Uri.EscapeDataString(correlationId)}", administratorToken))
            {
                Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
                var auditBody = await audit.Content.ReadAsStringAsync();
                Assert.Contains("retention.plan", auditBody, StringComparison.Ordinal);
                Assert.Contains("retention.execute-requested", auditBody, StringComparison.Ordinal);
                Assert.Contains("retention.execute-completed", auditBody, StringComparison.Ordinal);
                Assert.Contains(correlationId, auditBody, StringComparison.Ordinal);
                Assert.DoesNotContain(readerToken, auditBody, StringComparison.Ordinal);
            }

            await StopServerGracefullyAsync(server, client, shutdownToken);
            server.Dispose(); server = null;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
            server?.Dispose(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
