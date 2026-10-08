using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hispania.Identity.Application;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase21SecurityHardeningTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentLoginAndDisableCannotResurrectTokensAfterReenable()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase21-session-race-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "identity.sqlite"); var identity = new IdentityService(new SqliteIdentityStore(path));
            const string password = "test-only phase21 race passphrase";
            var user = (await identity.CreateUserAsync("race-user", "Race", password)).Value!;
            var timer = Stopwatch.StartNew();
            var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await new IdentityService(new SqliteIdentityStore(path)).LoginAsync("race-user", password))).ToArray();
            await identity.SetUserStatusAsync(user.Id, Hispania.Platform.Contracts.Identity.UserStatus.Disabled);
            var results = await Task.WhenAll(attempts);
            output.WriteLine("Eight concurrent PBKDF2 login attempts and disable: {0} ms (diagnostic baseline, no SLA).", timer.ElapsedMilliseconds);
            await identity.SetUserStatusAsync(user.Id, Hispania.Platform.Contracts.Identity.UserStatus.Enabled);
            var reopened = new IdentityService(new SqliteIdentityStore(path));
            foreach (var result in results.Where(r => r.Succeeded)) Assert.Null(await reopened.ValidateSessionAsync(result.SessionToken!));
            timer.Restart();
            Assert.True((await reopened.LoginAsync("race-user", password)).Succeeded);
            output.WriteLine("Normal successful PBKDF2 login: {0} ms; no SLA.", timer.ElapsedMilliseconds);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActualBackupAndRestoreRootsRestrictOtherIdentities()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation();
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        await BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default);
        foreach (var root in new[] { fixture.Bundle, fixture.Target })
        {
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
            else
            {
                var security = System.IO.FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root));
                Assert.True(security.AreAccessRulesProtected);
                var allowed = new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" };
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                    if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow) Assert.Contains(rule.IdentityReference.Value, allowed);
            }
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task HostileBackupResourceBoundsRejectBeforeRestoreCreatesTarget(bool excessiveCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase21-backup-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(root, "bundle"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(bundle);
        try
        {
            var files = Enumerable.Repeat(new { Path = "x", Size = excessiveCount ? 0L : 1024L * 1024 * 1024 * 1024 + 1 }, excessiveCount ? 65537 : 1).ToArray();
            var manifest = new { BackupFormatGeneration = 1, DistributionVersion = "0.7.0", BackupId = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow,
                ConsistencyMode = "offline-exclusive-installation-lock", Provider = "sqlite", StorageProvider = "filesystem", Files = files, RequiredComponents = Array.Empty<string>(), Reprovision = Array.Empty<string>(), Warnings = Array.Empty<string>() };
            await File.WriteAllTextAsync(Path.Combine(bundle, "manifest.json"), JsonSerializer.Serialize(manifest));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => BackupInstallation.RestoreAsync(bundle, target, default));
            Assert.Contains("resource policy", error.Message);
            Assert.False(Directory.Exists(target));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void BucketsRecoverWithoutAccountLockoutAndExpireWithBoundedMemory()
    {
        var clock = new ManualClock();
        var limiter = new SecurityAbuseLimiter(clock);
        for (var i = 0; i < 5; i++) Assert.Equal(0, limiter.Acquire("peer", "Alice"));
        Assert.Equal(10, limiter.Acquire("peer", "ALICE"));
        Assert.Equal(0, limiter.Acquire("other-peer", "alice"));
        clock.Advance(10);
        Assert.Equal(0, limiter.Acquire("peer", "alice"));
        for (var i = 0; i < 5000; i++) limiter.Acquire("peer-" + i);
        Assert.Equal(600, limiter.Acquire("new-peer"));
        clock.Advance(601);
        Assert.Equal(0, limiter.Acquire("new-peer"));
        for (var i = 0; i < 100; i++) Assert.True(limiter.TryRecordDeniedAudit());
        Assert.False(limiter.TryRecordDeniedAudit());
        clock.Advance(1);
        Assert.True(limiter.TryRecordDeniedAudit());
        using var metrics = new Hispania.Platform.Observability.Application.PlatformMetricsV1();
        foreach (var name in Hispania.Platform.Contracts.Observability.PlatformMetricNamesV1.Counters.Where(name => name.StartsWith("hispania.security.", StringComparison.Ordinal)))
        {
            metrics.Increment(name);
            Assert.Equal(1, Assert.Single(metrics.Snapshot(), row => row.Name == name).Value);
        }
        Assert.Equal(3, metrics.Snapshot().Count(row => row.Name.StartsWith("hispania.security.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OversizedPasswordIsRejectedAndRevocationSurvivesReconstruction()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase21-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "identity.sqlite");
            var store = new SqliteIdentityStore(path);
            var identity = new IdentityService(store);
            var user = (await identity.CreateUserAsync("alice", "Alice", "phase21 password canary")).Value!;
            var credential = (await store.GetCredentialAsync(user.Id, default))!;
            Assert.Equal(600_000, credential.Iterations);
            Assert.Equal(16, Convert.FromBase64String(credential.Salt).Length);
            Assert.False((await identity.LoginAsync("alice", new string('a', 1025))).Succeeded);
            var first = await identity.LoginAsync("alice", "phase21 password canary");
            var second = await identity.LoginAsync("alice", "phase21 password canary");
            Assert.NotEqual(first.SessionToken, second.SessionToken);
            Assert.Equal(43, first.SessionToken!.Length);
            Assert.Null(await store.GetSessionByTokenHashAsync(first.SessionToken, default));
            Assert.True(await identity.LogoutAsync(first.SessionToken));
            var reopened = new IdentityService(new SqliteIdentityStore(path));
            Assert.Null(await reopened.ValidateSessionAsync(first.SessionToken));
            Assert.NotNull(await reopened.ValidateSessionAsync(second.SessionToken!));
            await reopened.SetUserStatusAsync(user.Id, Hispania.Platform.Contracts.Identity.UserStatus.Disabled);
            Assert.Null(await identity.ValidateSessionAsync(second.SessionToken!));
            var disabled = await reopened.LoginAsync("alice", "phase21 password canary");
            var missing = await reopened.LoginAsync("unknown", "phase21 password canary");
            var wrong = await reopened.LoginAsync("alice", "wrong password canary");
            Assert.Equal(missing, disabled);
            Assert.Equal(missing, wrong);
            await reopened.SetUserStatusAsync(user.Id, Hispania.Platform.Contracts.Identity.UserStatus.Enabled);
            Assert.Null(await identity.ValidateSessionAsync(second.SessionToken!));
            Assert.True((await identity.LoginAsync("alice", "phase21 password canary")).Succeeded);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => timestamp;
        public void Advance(int seconds) => timestamp += seconds;
    }
}

public sealed partial class ServerProcessRestartTests
{
    [Fact]
    public async Task Phase21SecurityHardeningRealHttpRateLimitRecoveryAndForwardedSpoof()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase21-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var output = FindServerOutputDirectory(); var shutdown = Guid.NewGuid().ToString("N");
        var legacy = Guid.NewGuid().ToString("N"); var bootstrapToken = Guid.NewGuid().ToString("N");
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ReserveLoopbackPort()}"), Timeout = TimeSpan.FromSeconds(20) };
        Process? server = null;
        try
        {
            server = StartServer(Path.Combine(output, "Hispania.Server.dll"), output, root, library, legacy, bootstrapToken, shutdown, client.BaseAddress.Port);
            await WaitUntilHealthyAsync(server, client);
            using var oversized = await client.PostAsJsonAsync("/api/identity/login", new { username = "absent", password = new string('x', 9000) });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            for (var i = 0; i < 6; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity/login") { Content = JsonContent.Create(new { username = "absent", password = "canary-password-never-log" }) };
                request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
                request.Headers.Add("X-Forwarded-Proto", "https");
                using var response = await client.SendAsync(request);
                Assert.Equal(i < 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
                Assert.False(response.Headers.Contains("Strict-Transport-Security"));
                Assert.DoesNotContain("canary", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                if (i == 5)
                {
                    Assert.NotNull(response.Headers.RetryAfter?.Delta);
                    await Task.Delay(response.Headers.RetryAfter!.Delta!.Value + TimeSpan.FromMilliseconds(100));
                }
            }
            using var recovered = await client.PostAsJsonAsync("/api/identity/login", new { username = "absent", password = "canary-password-never-log" });
            Assert.Equal(HttpStatusCode.Unauthorized, recovered.StatusCode);
            foreach (var path in new[] { "/internal/metadata-enrichment", "/internal/capabilities/transcoding/health", "/api/platform/admin/overview" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            var session = await CreateAndLoginIdentityUserAsync(client, legacy);
            using var me = await SendWithSessionAsync(client, HttpMethod.Get, "/api/identity/me", session);
            using var user = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            var userId = user.RootElement.GetProperty("user").GetProperty("id").GetProperty("value").GetGuid();
            const string inventory = "/api/platform/admin/security/endpoints";
            using (var denied = await SendWithSessionAsync(client, HttpMethod.Get, inventory, session)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin") { Content = JsonContent.Create(new { userId }) };
            bootstrap.Headers.Authorization = new("Bootstrap", bootstrapToken);
            using (var granted = await client.SendAsync(bootstrap)) granted.EnsureSuccessStatusCode();
            using (var permitted = await SendWithSessionAsync(client, HttpMethod.Get, inventory, session))
            {
                permitted.EnsureSuccessStatusCode();
                using var rows = JsonDocument.Parse(await permitted.Content.ReadAsStringAsync());
                Assert.True(rows.RootElement.GetArrayLength() > 100);
                Assert.All(rows.RootElement.EnumerateArray(), row => Assert.NotEqual("UNCLASSIFIED", row.GetProperty("policy").GetString()));
                foreach (var row in rows.RootElement.EnumerateArray().Where(row => row.GetProperty("policy").GetString()!.StartsWith("permission:", StringComparison.Ordinal) && row.GetProperty("methods").EnumerateArray().Any(method => method.GetString() == "GET")))
                {
                    var route = System.Text.RegularExpressions.Regex.Replace(row.GetProperty("route").GetString()!, "\\{[^}]+\\}", "00000000-0000-0000-0000-000000000001");
                    using var anonymous = await client.GetAsync(route);
                    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                }
                var evidence = Environment.GetEnvironmentVariable("HISPANIA_SECURITY_EVIDENCE_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(evidence)) await File.WriteAllTextAsync(Path.Combine(evidence, "endpoint-inventory.json"), rows.RootElement.GetRawText());
            }
            using (var observed = await SendWithSessionAsync(client, HttpMethod.Get, "/api/platform/observability", session))
            {
                observed.EnsureSuccessStatusCode();
                using var snapshot = JsonDocument.Parse(await observed.Content.ReadAsStringAsync());
                var counters = snapshot.RootElement.GetProperty("metrics").EnumerateArray().ToArray();
                Assert.True(counters.Single(row => row.GetProperty("name").GetString() == "hispania.security.authentication.rate_limited").GetProperty("value").GetDouble() >= 1);
                Assert.True(counters.Single(row => row.GetProperty("name").GetString() == "hispania.security.authorization.denied").GetProperty("value").GetDouble() >= 3);
            }
            await StopServerGracefullyAsync(server, client, shutdown); server = null;
        }
        finally
        {
            if (server is not null) { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); server.Dispose(); }
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }
}
