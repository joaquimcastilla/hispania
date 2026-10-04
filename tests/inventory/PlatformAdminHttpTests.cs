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
