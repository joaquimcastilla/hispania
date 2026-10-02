using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class ServerProcessRestartTests
{
    [Fact]
    public async Task ScheduledPlatformJobSurvivesServerRestartAndRequiresSessionRbac()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-jobs-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var libraryRoot = Path.Combine(root, "empty-library"); Directory.CreateDirectory(libraryRoot);
        var serverDirectory = FindServerOutputDirectory(); var serverDll = Path.Combine(serverDirectory, "Hispania.Server.dll");
        var adminToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()); var bootstrapToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()); var shutdownToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var port = ReserveLoopbackPort(); using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) }; Process? server = null;
        try
        {
            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, bootstrapToken, shutdownToken, port); await WaitUntilHealthyAsync(server, client);
            var session = await CreateAndLoginIdentityUserAsync(client, adminToken);
            using var me = await SendWithSessionAsync(client, HttpMethod.Get, "/api/identity/me", session); using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            var userId = meJson.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid();
            using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) }; bootstrap.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", bootstrapToken);
            using (var bootstrapped = await client.SendAsync(bootstrap)) Assert.Equal(HttpStatusCode.OK, bootstrapped.StatusCode);
            using (var legacyJobs = new HttpRequestMessage(HttpMethod.Get, "/api/platform/jobs")) { legacyJobs.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken); using var rejected = await client.SendAsync(legacyJobs); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode); }
            using (var allowedJobs = await SendWithSessionAsync(client, HttpMethod.Get, "/api/platform/jobs", session)) Assert.Equal(HttpStatusCode.OK, allowedJobs.StatusCode);

            var scheduledAt = DateTimeOffset.UtcNow.AddSeconds(15);
            async Task<HttpResponseMessage> CreateJobAsync() { var req = new HttpRequestMessage(HttpMethod.Post, "/api/platform/jobs") { Content = JsonContent.Create(new { jobType = "platform.test.hash", payload = new { text = "restart-proof", algorithm = "sha256" }, scheduledAt, idempotencyKey = "server-restart-proof" }) }; req.Headers.Authorization = new AuthenticationHeaderValue("Session", session); return await client.SendAsync(req); }
            using var accepted = await CreateJobAsync(); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()); var jobId = acceptedJson.RootElement.GetProperty("jobId").GetGuid();
            Assert.False(acceptedJson.RootElement.TryGetProperty("payloadJson", out _));
            using var duplicate = await CreateJobAsync(); Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); using var duplicateJson = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync()); Assert.Equal(jobId, duplicateJson.RootElement.GetProperty("jobId").GetGuid());
            using var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/platform/jobs") { Content = JsonContent.Create(new { jobType = "platform.test.untrusted", payload = new { any = "value" } }) }; unknown.Headers.Authorization = new AuthenticationHeaderValue("Session", session); using (var unknownResponse = await client.SendAsync(unknown)) Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);

            await StopServerGracefullyAsync(server, client, shutdownToken); server.Dispose(); server = null;
            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, bootstrapToken, shutdownToken, port); await WaitUntilHealthyAsync(server, client);
            using var detailBeforeDue = await SendWithSessionAsync(client, HttpMethod.Get, $"/api/platform/jobs/{jobId:D}", session); detailBeforeDue.EnsureSuccessStatusCode(); using var detailJson = JsonDocument.Parse(await detailBeforeDue.Content.ReadAsStringAsync());
            Assert.Equal(jobId, detailJson.RootElement.GetProperty("jobId").GetGuid()); Assert.False(detailJson.RootElement.TryGetProperty("payloadJson", out _));
            using var completed = await WaitForJobStatusAsync(client, session, jobId, "Succeeded", TimeSpan.FromSeconds(30));
            using var attempts = await SendWithSessionAsync(client, HttpMethod.Get, $"/api/platform/jobs/{jobId:D}/attempts", session); attempts.EnsureSuccessStatusCode(); using var attemptJson = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync()); Assert.Single(attemptJson.RootElement.EnumerateArray());
            using var audit = await SendWithSessionAsync(client, HttpMethod.Get, $"/internal/admin/audit?domain=platform.jobs&resourceId={jobId:D}", session); audit.EnsureSuccessStatusCode();
            await StopServerGracefullyAsync(server, client, shutdownToken); server.Dispose(); server = null;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); } server?.Dispose(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<HttpResponseMessage> WaitForJobStatusAsync(HttpClient client, string token, Guid id, string expected, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow + timeout; string last = "no response";
        while (DateTimeOffset.UtcNow < until)
        {
            var response = await SendWithSessionAsync(client, HttpMethod.Get, $"/api/platform/jobs/{id:D}", token);
            if (response.IsSuccessStatusCode) { last = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(last); if (json.RootElement.GetProperty("status").GetString() == expected) return response; }
            else last = $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
            response.Dispose(); await Task.Delay(200);
        }
        throw new TimeoutException($"Platform Job did not reach {expected}. Last state: {last}");
    }

    [Fact]
    public async Task RealServerRestartsKeepInventoryAssetsAuditAndPlayableVideo()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-server-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var libraryRoot = Path.Combine(root, "empty-library"); Directory.CreateDirectory(libraryRoot);
        var serverDirectory = FindServerOutputDirectory();
        var serverDll = Path.Combine(serverDirectory, "Hispania.Server.dll");
        Assert.True(File.Exists(serverDll), $"Server output was not built: {serverDll}");
        var adminToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var shutdownToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var bootstrapToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var port = ReserveLoopbackPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");
        using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(5) };
        Process? server = null;
        try
        {
            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, bootstrapToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            var identityToken = await CreateAndLoginIdentityUserAsync(client, adminToken);
            await AssertAuthorizationApiAsync(client, identityToken, adminToken, bootstrapToken, root);
            var first = await AssertPersistentStateAsync(client, identityToken, adminToken, expectedStock: 12, expectedMinimumAuditRecords: 5);
            await AssertAdminWritesAndAssociationAsync(client, adminToken, first.ItemId, serverDirectory);
            first = await AssertPersistentStateAsync(client, identityToken, adminToken, expectedStock: 12, expectedMinimumAuditRecords: 8);
            await StopServerGracefullyAsync(server, client, shutdownToken);
            server.Dispose(); server = null;

            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, bootstrapToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            await AssertIdentitySessionAsync(client, identityToken, expectedStatus: HttpStatusCode.OK);
            var afterOrderlyRestart = await AssertPersistentStateAsync(client, identityToken, adminToken, expectedStock: 12, expectedMinimumAuditRecords: 8);
            Assert.Equal(first.ItemId, afterOrderlyRestart.ItemId);
            Assert.Equal(first.AssetIds.Order(), afterOrderlyRestart.AssetIds.Order());

            await IssueOneUnitAsync(client, adminToken, afterOrderlyRestart.ItemId, afterOrderlyRestart.LocationId);
            _ = await AssertPersistentStateAsync(client, identityToken, adminToken, expectedStock: 11, expectedMinimumAuditRecords: 9);
            using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/identity/logout"))
            {
                logout.Headers.Authorization = new AuthenticationHeaderValue("Session", identityToken);
                using var logoutResponse = await client.SendAsync(logout);
                Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
            }
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            server.Dispose(); server = null;

            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, bootstrapToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            await AssertIdentitySessionAsync(client, identityToken, expectedStatus: HttpStatusCode.Unauthorized);
            var reauthenticatedToken = await LoginRestartUserForTokenAsync(client);
            var afterAbruptRestart = await AssertPersistentStateAsync(client, reauthenticatedToken, adminToken, expectedStock: 11, expectedMinimumAuditRecords: 9);
            Assert.Equal(first.ItemId, afterAbruptRestart.ItemId);
            Assert.Equal(first.AssetIds.Order(), afterAbruptRestart.AssetIds.Order());
            AssertStoreOwnership(root);
            await StopServerGracefullyAsync(server, client, shutdownToken);
            server.Dispose(); server = null;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
            server?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CreateAndLoginIdentityUserAsync(HttpClient client, string adminToken)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/identity/users")
        {
            Content = JsonContent.Create(new { username = "RestartUser", displayName = "Restart User", password = "a persistent test passphrase" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using (var legacyTokenAsSession = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me"))
        {
            legacyTokenAsSession.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var rejected = await client.SendAsync(legacyTokenAsSession);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        using var login = await client.PostAsJsonAsync("/api/identity/login", new { username = "restartuser", password = "a persistent test passphrase" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = Assert.IsType<string>(loginJson.RootElement.GetProperty("sessionToken").GetString());
        using var wrongPassword = await client.PostAsJsonAsync("/api/identity/login", new { username = "restartuser", password = "wrong persistent test passphrase" });
        using var unknownUser = await client.PostAsJsonAsync("/api/identity/login", new { username = "unknownuser", password = "wrong persistent test passphrase" });
        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
        return token;
    }

    private static async Task AssertIdentitySessionAsync(HttpClient client, string token, HttpStatusCode expectedStatus)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Session", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        if (response.IsSuccessStatusCode)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("restartuser", json.RootElement.GetProperty("user").GetProperty("username").GetString());
        }
    }

    private static async Task AssertAuthorizationApiAsync(HttpClient client, string sessionToken, string legacyToken, string bootstrapToken, string root)
    {
        using (var noSession = await client.GetAsync("/api/authorization/users")) Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        using (var invalidSession = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            invalidSession.Headers.Authorization = new AuthenticationHeaderValue("Session", "invalid-session-token-which-is-long-enough");
            using var rejected = await client.SendAsync(invalidSession); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using (var forbidden = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            forbidden.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var denied = await client.SendAsync(forbidden); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me"); me.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
        using var meResponse = await client.SendAsync(me); meResponse.EnsureSuccessStatusCode();
        using var meJson = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        var userId = meJson.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid();
        using (var wrongBootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) })
        {
            wrongBootstrap.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", Convert.ToHexString(Guid.NewGuid().ToByteArray()));
            using var rejected = await client.SendAsync(wrongBootstrap); Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
        using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) };
        bootstrap.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", bootstrapToken);
        using var bootstrapped = await client.SendAsync(bootstrap); Assert.Equal(HttpStatusCode.OK, bootstrapped.StatusCode);

        var protectedMediaId = Guid.NewGuid();
        foreach (var path in new[] { "/api/inventory/items", "/api/v1/media", $"/api/v1/media/{protectedMediaId:D}/stream", $"/api/v1/media/{protectedMediaId:D}/metadata/edit" })
        {
            using var noSession = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
            using var legacyOnly = new HttpRequestMessage(HttpMethod.Get, path);
            legacyOnly.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            using var legacyDenied = await client.SendAsync(legacyOnly);
            Assert.Equal(HttpStatusCode.Unauthorized, legacyDenied.StatusCode);
            using var missingGrant = new HttpRequestMessage(HttpMethod.Get, path);
            missingGrant.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var grantDenied = await client.SendAsync(missingGrant);
            Assert.Equal(HttpStatusCode.Forbidden, grantDenied.StatusCode);
        }
        using var preWrite = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/items") { Content = JsonContent.Create(new { sku = "AUTHZ-BEFORE", name = "Denied" }) };
        preWrite.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
        using (var deniedWrite = await client.SendAsync(preWrite)) Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);

        using var roleListRequest = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/roles");
        roleListRequest.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
        using var roleListResponse = await client.SendAsync(roleListRequest); roleListResponse.EnsureSuccessStatusCode();
        using var rolesJson = JsonDocument.Parse(await roleListResponse.Content.ReadAsStringAsync());
        var administratorRoleId = rolesJson.RootElement.EnumerateArray().Single(role => role.GetProperty("role").GetProperty("name").GetString() == "administrator").GetProperty("role").GetProperty("id").GetProperty("value").GetGuid();
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "inventory.read");
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "media.library.read");
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "media.playback.use");
        using (var eventsLegacy = new HttpRequestMessage(HttpMethod.Get, "/internal/admin/events/deliveries"))
        {
            eventsLegacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            using var rejectedEventsBearer = await client.SendAsync(eventsLegacy);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedEventsBearer.StatusCode);
        }
        using (var inventoryRead = await SendWithSessionAsync(client, HttpMethod.Get, "/api/inventory/items", sessionToken)) Assert.Equal(HttpStatusCode.OK, inventoryRead.StatusCode);
        using (var mediaRead = await SendWithSessionAsync(client, HttpMethod.Get, "/api/v1/media", sessionToken)) Assert.Equal(HttpStatusCode.OK, mediaRead.StatusCode);
        using (var playbackGranted = await SendWithSessionAsync(client, HttpMethod.Get, $"/api/v1/media/{protectedMediaId:D}/stream", sessionToken)) Assert.Equal(HttpStatusCode.NotFound, playbackGranted.StatusCode);
        using (var adminDenied = await SendWithSessionAsync(client, HttpMethod.Get, $"/api/v1/media/{protectedMediaId:D}/metadata/edit", sessionToken)) Assert.Equal(HttpStatusCode.Forbidden, adminDenied.StatusCode);
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "inventory.write");
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "platform.events.read");
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "platform.events.manage");
        using (var durableDeliveries = await SendWithSessionAsync(client, HttpMethod.Get, "/internal/admin/events/deliveries", sessionToken))
        {
            durableDeliveries.EnsureSuccessStatusCode();
            using var deliveryRows = JsonDocument.Parse(await durableDeliveries.Content.ReadAsStringAsync());
            var stockDelivery = deliveryRows.RootElement.EnumerateArray().FirstOrDefault(row => row.GetProperty("eventType").GetString() == "inventory.stock-changed" && row.GetProperty("subscriberId").GetString() == "platform.inventory.stock-observer");
            Assert.NotEqual(JsonValueKind.Undefined, stockDelivery.ValueKind);
            Assert.Equal("Delivered", stockDelivery.GetProperty("status").GetString());
            using var detail = await SendWithSessionAsync(client, HttpMethod.Get, $"/internal/admin/events/{stockDelivery.GetProperty("eventId").GetGuid():D}", sessionToken);
            detail.EnsureSuccessStatusCode();
        }
        using (var postWrite = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/items") { Content = JsonContent.Create(new { sku = "AUTHZ-AFTER", name = "Granted in active session" }) })
        {
            postWrite.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var created = await client.SendAsync(postWrite);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        await GrantPermissionAsync(client, sessionToken, administratorRoleId, "media.admin");
        using (var adminGranted = await SendWithSessionAsync(client, HttpMethod.Get, $"/api/v1/media/{protectedMediaId:D}/metadata/edit", sessionToken)) Assert.Equal(HttpStatusCode.NotFound, adminGranted.StatusCode);
        using (var adminOperation = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/media/{protectedMediaId:D}/metadata/tmdb/select") { Content = JsonContent.Create(new { tmdbMovieId = "42" }) })
        {
            adminOperation.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var missingMedia = await client.SendAsync(adminOperation);
            Assert.Equal(HttpStatusCode.NotFound, missingMedia.StatusCode);
        }
        using (var mediaAuditRequest = new HttpRequestMessage(HttpMethod.Get, "/internal/admin/audit?domain=media&page=1&pageSize=100"))
        {
            mediaAuditRequest.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var mediaAuditResponse = await client.SendAsync(mediaAuditRequest); mediaAuditResponse.EnsureSuccessStatusCode();
            using var mediaAudit = JsonDocument.Parse(await mediaAuditResponse.Content.ReadAsStringAsync());
            Assert.Contains(mediaAudit.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("domain").GetString() == "media" && item.GetProperty("actorId").GetString() == userId.ToString("D"));
        }

        using (var granted = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            granted.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var allowed = await client.SendAsync(granted); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        using (var legacy = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            legacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            using var allowed = await client.SendAsync(legacy); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var createdUser = new HttpRequestMessage(HttpMethod.Post, "/api/identity/users") { Content = JsonContent.Create(new { username = "sessioncheck", displayName = "Session Check", password = "a sufficiently long passphrase" }) };
        createdUser.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
        using var userResponse = await client.SendAsync(createdUser); Assert.Equal(HttpStatusCode.Created, userResponse.StatusCode);
        using var createdUserJson = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
        var sessionUserId = createdUserJson.RootElement.GetProperty("id").GetProperty("value").GetGuid();
        using (var assignRole = new HttpRequestMessage(HttpMethod.Post, $"/api/authorization/users/{sessionUserId:D}/roles/{administratorRoleId:D}"))
        {
            assignRole.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var assigned = await client.SendAsync(assignRole); Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        }

        var expiredToken = await LoginForTokenAsync(client);
        await ExpireSessionTokenAsync(Path.Combine(root, "platform", "identity.sqlite"), expiredToken);
        using (var expiredAccess = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            expiredAccess.Headers.Authorization = new AuthenticationHeaderValue("Session", expiredToken);
            using var rejected = await client.SendAsync(expiredAccess); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var revokedToken = await LoginForTokenAsync(client);
        using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/identity/logout"))
        {
            logout.Headers.Authorization = new AuthenticationHeaderValue("Session", revokedToken);
            using var loggedOut = await client.SendAsync(logout); Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        }
        using (var revokedAccess = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            revokedAccess.Headers.Authorization = new AuthenticationHeaderValue("Session", revokedToken);
            using var rejected = await client.SendAsync(revokedAccess); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var disabledToken = await LoginForTokenAsync(client);
        using (var disable = new HttpRequestMessage(HttpMethod.Patch, $"/api/authorization/users/{sessionUserId:D}/status") { Content = JsonContent.Create(new { enabled = false }) })
        {
            disable.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
            using var disabled = await client.SendAsync(disable); Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        }
        using (var disabledAccess = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/users"))
        {
            disabledAccess.Headers.Authorization = new AuthenticationHeaderValue("Session", disabledToken);
            using var rejected = await client.SendAsync(disabledAccess); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        foreach (var protectedPath in new[] { "/api/inventory/items", "/api/v1/media", $"/api/v1/media/{protectedMediaId:D}/stream", $"/api/v1/media/{protectedMediaId:D}/metadata/edit" })
        {
            using var disabledDomainAccess = new HttpRequestMessage(HttpMethod.Get, protectedPath);
            disabledDomainAccess.Headers.Authorization = new AuthenticationHeaderValue("Session", disabledToken);
            using var disabledDomainDenied = await client.SendAsync(disabledDomainAccess);
            Assert.Equal(HttpStatusCode.Unauthorized, disabledDomainDenied.StatusCode);
        }
        using var bootstrapAgain = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) };
        bootstrapAgain.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", bootstrapToken);
        using var rejectedAgain = await client.SendAsync(bootstrapAgain); Assert.Equal(HttpStatusCode.Forbidden, rejectedAgain.StatusCode);
    }

    private static async Task<string> LoginForTokenAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/identity/login", new { username = "sessioncheck", password = "a sufficiently long passphrase" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return Assert.IsType<string>(json.RootElement.GetProperty("sessionToken").GetString());
    }

    private static async Task<string> LoginRestartUserForTokenAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/identity/login", new { username = "restartuser", password = "a persistent test passphrase" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return Assert.IsType<string>(json.RootElement.GetProperty("sessionToken").GetString());
    }

    private static async Task GrantPermissionAsync(HttpClient client, string sessionToken, Guid roleId, string permission)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/authorization/roles/{roleId:D}/permissions/{Uri.EscapeDataString(permission)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendWithSessionAsync(HttpClient client, HttpMethod method, string path, string sessionToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
        return client.SendAsync(request);
    }

    private static async Task ExpireSessionTokenAsync(string databasePath, string token)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE identity_sessions SET expires_utc=$expires WHERE token_hash=$tokenHash";
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        command.Parameters.AddWithValue("$tokenHash", tokenHash);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(Guid ItemId, Guid LocationId, Guid[] AssetIds)> AssertPersistentStateAsync(HttpClient client, string sessionToken, string adminToken, int expectedStock, int expectedMinimumAuditRecords)
    {
        using var itemsDoc = await GetJsonAsync(client, "/api/inventory/items", sessionToken);
        var item = Assert.Single(itemsDoc.RootElement.EnumerateArray(), element => element.GetProperty("sku").GetString() == "BP48");
        var itemId = ReadWrappedId(item.GetProperty("id"));
        var references = item.GetProperty("assets").EnumerateArray().Select(element => ReadWrappedId(element.GetProperty("id"))).ToArray();
        Assert.True(references.Length >= 3);

        using var warehousesDoc = await GetJsonAsync(client, "/api/inventory/warehouses", sessionToken);
        var warehouse = Assert.Single(warehousesDoc.RootElement.EnumerateArray(), element => element.GetProperty("code").GetString() == "MAD");
        Assert.Equal("Madrid Warehouse", warehouse.GetProperty("name").GetString());
        using var locationsDoc = await GetJsonAsync(client, "/api/inventory/locations", sessionToken);
        var location = Assert.Single(locationsDoc.RootElement.EnumerateArray(), element => element.GetProperty("code").GetString() == "A-01");
        var locationId = ReadWrappedId(location.GetProperty("id"));
        Assert.Equal(expectedStock, await GetStockAsync(client, itemId, sessionToken));
        using var movementsDoc = await GetJsonAsync(client, $"/api/inventory/items/{itemId:D}/movements", sessionToken);
        Assert.True(movementsDoc.RootElement.GetArrayLength() >= (expectedStock == 12 ? 1 : 2));

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videoId = Guid.Empty;
        foreach (var assetId in references)
        {
            using var metadata = await GetJsonAsync(client, $"/api/assets/{assetId:D}");
            var type = metadata.RootElement.GetProperty("type").GetString()!;
            types.Add(type);
            using var contentResponse = await client.GetAsync($"/api/assets/{assetId:D}/content");
            contentResponse.EnsureSuccessStatusCode();
            var bytes = await contentResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(metadata.RootElement.GetProperty("size").GetInt64(), bytes.LongLength);
            if (type.Equals("Image", StringComparison.OrdinalIgnoreCase)) Assert.StartsWith("<svg", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            if (type.Equals("Document", StringComparison.OrdinalIgnoreCase)) Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
            if (type.Equals("Video", StringComparison.OrdinalIgnoreCase))
            {
                videoId = assetId;
                Assert.True(bytes.Length > 24);
                Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
            }
        }
        Assert.Contains("Image", types); Assert.Contains("Document", types); Assert.Contains("Video", types);

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/internal/admin/audit?domain=inventory&page=1&pageSize=100");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var auditResponse = await client.SendAsync(auditRequest); auditResponse.EnsureSuccessStatusCode();
        using var auditDoc = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync());
        var auditItems = auditDoc.RootElement.GetProperty("items");
        Assert.True(auditItems.GetArrayLength() >= expectedMinimumAuditRecords);
        Assert.Contains(auditItems.EnumerateArray(), record => record.GetProperty("resourceType").GetString() == "stock-movement");
        using var currentUser = await GetJsonAsync(client, "/api/identity/me", sessionToken);
        var actorId = currentUser.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid().ToString("D");
        Assert.Contains(auditItems.EnumerateArray(), record => record.GetProperty("domain").GetString() == "inventory" && record.GetProperty("actorId").GetString() == actorId);

        using var unauthorizedAudit = await client.GetAsync("/internal/admin/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAudit.StatusCode);
        using var playback = await GetJsonAsync(client, $"/api/media/assets/{videoId:D}/playback", sessionToken);
        Assert.True(playback.RootElement.GetProperty("available").GetBoolean());
        var streamUrl = playback.RootElement.GetProperty("streamUrl").GetString()!;
        using var videoResponse = await SendWithSessionAsync(client, HttpMethod.Get, streamUrl, sessionToken); videoResponse.EnsureSuccessStatusCode();
        Assert.Equal("video/mp4", videoResponse.Content.Headers.ContentType?.MediaType);
        var streamBytes = await videoResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal("ftyp", Encoding.ASCII.GetString(streamBytes, 4, 4));
        return (itemId, locationId, references);
    }

    private static async Task AssertAdminWritesAndAssociationAsync(HttpClient client, string adminToken, Guid itemId, string serverDirectory)
    {
        using var noAuth = await client.PostAsync("/api/assets", new ByteArrayContent([0x25, 0x50]));
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        using var badType = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new ByteArrayContent([1, 2, 3]) };
        badType.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        badType.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        badType.Headers.Add("X-Asset-Type", "Image"); badType.Headers.Add("X-Asset-Domain", "erp.inventory"); badType.Headers.Add("X-Asset-Owner", itemId.ToString("D"));
        using var badTypeResponse = await client.SendAsync(badType); Assert.Equal(HttpStatusCode.BadRequest, badTypeResponse.StatusCode);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new ByteArrayContent(new byte[1024 * 1024 + 1]) };
        oversized.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        oversized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        oversized.Headers.Add("X-Asset-Type", "Document"); oversized.Headers.Add("X-Asset-Domain", "erp.inventory"); oversized.Headers.Add("X-Asset-Owner", itemId.ToString("D"));
        using var oversizedResponse = await client.SendAsync(oversized); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

        var pdf = await File.ReadAllBytesAsync(Path.Combine(serverDirectory, "demo-assets", "bp48-datasheet.pdf"));
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new ByteArrayContent(pdf) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        upload.Headers.Add("X-Asset-Type", "Document"); upload.Headers.Add("X-Asset-Domain", "erp.inventory");
        upload.Headers.Add("X-Asset-Owner", itemId.ToString("D")); upload.Headers.Add("X-Asset-Public", "true");
        using var uploadResponse = await client.SendAsync(upload); Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        using var uploaded = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        var assetId = ReadWrappedId(uploaded.RootElement.GetProperty("id"));

        using var unauthAssociation = await client.PostAsJsonAsync($"/api/inventory/items/{itemId:D}/assets", new { assetId });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthAssociation.StatusCode);
        using var association = new HttpRequestMessage(HttpMethod.Post, $"/api/inventory/items/{itemId:D}/assets") { Content = JsonContent.Create(new { assetId }) };
        association.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var associationResponse = await client.SendAsync(association); Assert.Equal(HttpStatusCode.NoContent, associationResponse.StatusCode);
    }

    private static async Task IssueOneUnitAsync(HttpClient client, string adminToken, Guid itemId, Guid locationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/issue") { Content = JsonContent.Create(new { itemId, locationId, quantity = 1 }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
    }

    private static async Task<int> GetStockAsync(HttpClient client, Guid itemId, string sessionToken)
    {
        using var stock = await GetJsonAsync(client, "/api/inventory/stock", sessionToken);
        return stock.RootElement.EnumerateArray().Where(level => ReadWrappedId(level.GetProperty("itemId")) == itemId).Sum(level => level.GetProperty("quantity").GetInt32());
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, string? sessionToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sessionToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Session", sessionToken);
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Guid ReadWrappedId(JsonElement id) => Guid.Parse(id.GetProperty("value").GetString()!);

    private static Process StartServer(string serverDll, string workingDirectory, string root, string libraryRoot, string adminToken, string bootstrapToken, string shutdownToken, int port)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(serverDll);
        start.ArgumentList.Add($"--urls=http://127.0.0.1:{port}");
        start.ArgumentList.Add($"--Persistence:Sqlite:Path={Path.Combine(root, "media", "catalog.sqlite")}");
        start.ArgumentList.Add($"--Inventory:Sqlite:Path={Path.Combine(root, "inventory", "inventory.sqlite")}");
        start.ArgumentList.Add($"--Platform:DataPath={Path.Combine(root, "platform")}");
        start.ArgumentList.Add("--Platform:Assets:MaximumBytes=1048576");
        start.ArgumentList.Add($"--DistributedWorkers:StatePath={Path.Combine(root, "workers", "coordinator.sqlite")}");
        start.ArgumentList.Add($"--DistributedWorkers:TransferPath={Path.Combine(root, "workers", "transfers")}");
        start.ArgumentList.Add($"--Library:Roots:0:Key=demo"); start.ArgumentList.Add($"--Library:Roots:0:Path={libraryRoot}");
        start.ArgumentList.Add("--Ffprobe:ExecutablePath=ffprobe"); start.ArgumentList.Add("--Ffmpeg:ExecutablePath=ffmpeg");
        start.ArgumentList.Add($"--Playback:TranscodeCachePath={Path.Combine(root, "media", "transcodes")}");
        start.ArgumentList.Add($"--PlaybackExperiments:StatePath={Path.Combine(root, "platform", "playback-experiments.json")}");
        start.ArgumentList.Add($"--Modules:AdminToken={adminToken}"); start.Environment["Identity__Bootstrap__Token"] = bootstrapToken; start.ArgumentList.Add($"--Testing:ShutdownToken={shutdownToken}");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Hispania.Server process.");
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitUntilHealthyAsync(Process process, HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45); Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Hispania.Server exited early with code {process.ExitCode}.");
            try { using var response = await client.GetAsync("/healthz"); if (response.IsSuccessStatusCode) return; }
            catch (HttpRequestException exception) { lastError = exception; }
            catch (TaskCanceledException exception) { lastError = exception; }
            await Task.Delay(250);
        }
        throw new TimeoutException("Hispania.Server did not become healthy within 45 seconds.", lastError);
    }

    private static async Task StopServerGracefullyAsync(Process process, HttpClient client, string shutdownToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/shutdown");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shutdownToken);
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindServerOutputDirectory()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(root, "apps", "server", "bin", configuration, "net10.0");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void AssertStoreOwnership(string root)
    {
        var inventory = Path.GetFullPath(Path.Combine(root, "inventory", "inventory.sqlite"));
        var assets = Path.GetFullPath(Path.Combine(root, "platform", "assets.sqlite"));
        var audit = Path.GetFullPath(Path.Combine(root, "platform", "audit.sqlite"));
        var identity = Path.GetFullPath(Path.Combine(root, "platform", "identity.sqlite"));
        var media = Path.GetFullPath(Path.Combine(root, "media", "catalog.sqlite"));
        Assert.True(File.Exists(inventory)); Assert.True(File.Exists(assets)); Assert.True(File.Exists(audit)); Assert.True(File.Exists(identity)); Assert.True(File.Exists(media));
        Assert.NotEqual(inventory, assets); Assert.NotEqual(assets, audit); Assert.NotEqual(audit, identity); Assert.NotEqual(identity, media);
        Assert.True(Directory.EnumerateFiles(Path.Combine(root, "platform", "assets"), "*.blob").Any());
        Assert.False(Directory.EnumerateFiles(Path.Combine(root, "inventory"), "*.blob").Any());
    }
}
