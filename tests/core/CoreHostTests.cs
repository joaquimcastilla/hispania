using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Hispania.Core.Identity;
using Hispania.Core.Http;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class CoreHostTests : IDisposable
{
    private readonly List<string> _temporaryDataDirectories = [];
    private readonly List<string> _temporaryCertificateFiles = [];
    private readonly Dictionary<int, string> _certificateThumbprints = [];
    private string? _lastDataDirectory;

    [Fact]
    public void DefaultsUseLoopbackAndHaveNoDomainOrCredentialConfiguration()
    {
        var options = CoreHostOptions.Read(new ConfigurationBuilder().Build(), "Production");
        Assert.Equal(IPAddress.Loopback, options.ListenAddress);
        Assert.Equal(5070, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ShutdownTimeout);
        Assert.Equal(LogLevel.Information, options.MinimumLogLevel);
        Assert.Equal(20, options.LoginRateLimit);
        Assert.Equal(60, options.LoginRateWindowSeconds);
        Assert.False(string.Equals(options.Storage.DataDirectory, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(options.Version));
        Assert.DoesNotContain(typeof(CoreHost).Assembly.GetReferencedAssemblies(), reference =>
            new[] { "Hispania.Server", "Hispania.Inventory", "Hispania.Media", "Hispania.WorkerAgent", "Npgsql" }
                .Contains(reference.Name, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.50")]
    [InlineData("not-an-ip")]
    public void NonLoopbackOrInvalidAddressFailsClosed(string address)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LISTEN_ADDRESS"] = address }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidPortFailsConfiguration(string port)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PORT"] = port }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("invalid")]
    public void InvalidShutdownTimeoutFailsConfiguration(string timeout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SHUTDOWN_TIMEOUT_SECONDS"] = timeout }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("invalid")]
    public void InvalidLogLevelFailsConfiguration(string logLevel)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LOG_LEVEL"] = logLevel }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Theory]
    [InlineData("LOGIN_RATE_LIMIT", "0")]
    [InlineData("LOGIN_RATE_LIMIT", "1001")]
    [InlineData("LOGIN_RATE_WINDOW_SECONDS", "0")]
    [InlineData("LOGIN_RATE_WINDOW_SECONDS", "3601")]
    public void InvalidLoginRateLimitConfigurationFailsClosed(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Throws<CoreConfigurationException>(() => CoreHostOptions.Read(config, "Production"));
    }

    [Fact]
    public void HttpHostRequiresAnExplicitHttpsCertificate()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "hispania-core-host-tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<CoreConfigurationException>(() => CoreHost.Build(new Dictionary<string, string?>
        {
            ["DATA_DIRECTORY"] = dataDirectory,
            ["PORT"] = GetAvailableLoopbackPort().ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
    }

    [Fact]
    public void LogLevelCanBeConfiguredExplicitly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LOG_LEVEL"] = "Debug" }).Build();
        Assert.Equal(LogLevel.Debug, CoreHostOptions.Read(config, "Development").MinimumLogLevel);
    }

    [Fact]
    public async Task OnlyExplicitLivenessGetIsPublicAndReturnsStableGenericStatus()
    {
        var port = GetAvailableLoopbackPort();
        await using var app = Build(port);
        await app.StartAsync();
        using var client = CreateClient(port);

        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"healthy\"}", json);
        Assert.DoesNotContain("admin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);

        using var post = await client.PostAsync("/health/live", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        using var admin = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        using var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);

        await app.StopAsync();
        Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationStopsTheHostCleanly()
    {
        var port = GetAvailableLoopbackPort();
        await using var app = Build(port);
        using var cancellation = new CancellationTokenSource();
        await app.StartAsync();
        var run = app.WaitForShutdownAsync(cancellation.Token);
        using var client = CreateClient(port);
        using var response = await WaitForHealthAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(app.Lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task OccupiedPortFailsStartupWithoutStartingTheHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var app = Build(port);
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
        Assert.False(app.Lifetime.ApplicationStarted.IsCancellationRequested);
    }

    [Fact]
    public async Task HttpsLoginMeAndLogoutUseSecureCookieAndCsrfProtection()
    {
        var port = GetAvailableLoopbackPort();
        await using var app = Build(port);
        await app.StartAsync();
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        const string password = "correct horse battery staple 2026";
        Assert.Equal(CoreBootstrapResult.Created,
            await bootstrap.CreateInitialAdministratorAsync("owner", password, CancellationToken.None));

        using var client = CreateClient(port);
        var origin = $"https://127.0.0.1:{port}";
        using (var httpClient = new HttpClient())
            await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.GetAsync($"http://127.0.0.1:{port}/health/live"));
        using var anonymousMe = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousMe.StatusCode);
        using (var bearerOnly = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me"))
        {
            bearerOnly.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new string('a', 43));
            using var bearerResponse = await client.SendAsync(bearerOnly);
            Assert.Equal(HttpStatusCode.Unauthorized, bearerResponse.StatusCode);
        }
        using var anonymousLogout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        anonymousLogout.Headers.TryAddWithoutValidation("Origin", origin);
        using var anonymousLogoutResponse = await client.SendAsync(anonymousLogout);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousLogoutResponse.StatusCode);

        using var hostileLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "owner", password })
        };
        hostileLogin.Headers.TryAddWithoutValidation("Origin", "https://attacker.invalid");
        using var hostileResponse = await client.SendAsync(hostileLogin);
        Assert.Equal(HttpStatusCode.Forbidden, hostileResponse.StatusCode);
        Assert.False(hostileResponse.Headers.Contains("Access-Control-Allow-Origin"));

        using var malformedLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = new StringContent("{", System.Text.Encoding.UTF8, "application/json") };
        malformedLogin.Headers.TryAddWithoutValidation("Origin", origin);
        using var malformedResponse = await client.SendAsync(malformedLogin);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        using var excessiveLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = new string('a', 65), password })
        };
        excessiveLogin.Headers.TryAddWithoutValidation("Origin", origin);
        using var excessiveResponse = await client.SendAsync(excessiveLogin);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveResponse.StatusCode);
        using var oversizedLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent("{\"username\":\"" + new string('a', 5000) + "\",\"password\":\"unused\"}", System.Text.Encoding.UTF8, "application/json")
        };
        oversizedLogin.Headers.TryAddWithoutValidation("Origin", origin);
        using var oversizedResponse = await client.SendAsync(oversizedLogin);
        Assert.Equal((HttpStatusCode)413, oversizedResponse.StatusCode);

        using var wrongPassword = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "owner", password = "incorrect horse battery staple" })
        };
        wrongPassword.Headers.TryAddWithoutValidation("Origin", origin);
        using var wrongPasswordResponse = await client.SendAsync(wrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);
        var wrongPasswordBody = await wrongPasswordResponse.Content.ReadAsStringAsync();

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "owner", password })
        };
        login.Headers.TryAddWithoutValidation("Origin", origin);
        using var loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.Contains("no-store", loginResponse.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        var setCookies = loginResponse.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = Assert.Single(setCookies, value => value.StartsWith("__Secure-HispaniaSession=", StringComparison.Ordinal));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        var sessionSecretForReplayTest = sessionCookie.Split(';', 2)[0].Split('=', 2)[1];
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<CoreAuthEndpoints.LoginResponse>();
        Assert.NotNull(loginBody);
        Assert.False(string.IsNullOrWhiteSpace(loginBody.CsrfToken));
        Assert.DoesNotContain(password, await loginResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var identityService = app.Services.GetRequiredService<ICoreIdentityService>();
        const string secondPassword = "another account password 2026";
        var secondIdentity = await identityService.CreateLocalAccountAsync(loginBody.SubjectId, "second-user", secondPassword, CancellationToken.None);
        Assert.NotNull(secondIdentity);
        using (var secondLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "second-user", password = secondPassword })
        })
        {
            secondLogin.Headers.TryAddWithoutValidation("Origin", origin);
            using var secondLoginResponse = await client.SendAsync(secondLogin);
            Assert.Equal(HttpStatusCode.OK, secondLoginResponse.StatusCode);
        }
        using (var secondMe = await client.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, secondMe.StatusCode);
            var secondMeBody = await secondMe.Content.ReadFromJsonAsync<CoreAuthEndpoints.MeResponse>();
            Assert.NotNull(secondMeBody);
            Assert.Empty(secondMeBody.Permissions);
        }
        Assert.True(await identityService.SetAccountStatusAsync(loginBody.SubjectId, secondIdentity.SubjectId, CoreAccountStatus.Disabled, CancellationToken.None));
        using (var disabledMe = await client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, disabledMe.StatusCode);

        using var ownerRelogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "owner", password })
        };
        ownerRelogin.Headers.TryAddWithoutValidation("Origin", origin);
        using var ownerReloginResponse = await client.SendAsync(ownerRelogin);
        Assert.Equal(HttpStatusCode.OK, ownerReloginResponse.StatusCode);
        loginBody = await ownerReloginResponse.Content.ReadFromJsonAsync<CoreAuthEndpoints.LoginResponse>();
        Assert.NotNull(loginBody);
        var ownerSessionCookie = Assert.Single(ownerReloginResponse.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Secure-HispaniaSession=", StringComparison.Ordinal));
        sessionSecretForReplayTest = ownerSessionCookie.Split(';', 2)[0].Split('=', 2)[1];

        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains("no-store", me.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        var meBody = await me.Content.ReadFromJsonAsync<CoreAuthEndpoints.MeResponse>();
        Assert.NotNull(meBody);
        Assert.Equal("owner", meBody.Username);
        Assert.Contains(CoreIdentityRules.IdentityManagePermission, meBody.Permissions);
        Assert.DoesNotContain("token", await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var csrfInvalidLogout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        csrfInvalidLogout.Headers.TryAddWithoutValidation("Origin", origin);
        using var csrfInvalidResponse = await client.SendAsync(csrfInvalidLogout);
        Assert.Equal(HttpStatusCode.BadRequest, csrfInvalidResponse.StatusCode);

        using var crossSiteLogout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        crossSiteLogout.Headers.TryAddWithoutValidation("Origin", "https://attacker.invalid");
        crossSiteLogout.Headers.TryAddWithoutValidation("X-Hispania-CSRF", loginBody.CsrfToken);
        using var crossSiteLogoutResponse = await client.SendAsync(crossSiteLogout);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteLogoutResponse.StatusCode);
        using (var stillValid = await client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.TryAddWithoutValidation("Origin", origin);
        logout.Headers.TryAddWithoutValidation("X-Hispania-CSRF", loginBody.CsrfToken);
        using var logoutResponse = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        using var replayedSession = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        replayedSession.Headers.TryAddWithoutValidation("Cookie", $"__Secure-HispaniaSession={sessionSecretForReplayTest}");
        using var revokedMe = await client.SendAsync(replayedSession);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedMe.StatusCode);

        using var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "missing", password = "incorrect horse battery staple" })
        };
        unknown.Headers.TryAddWithoutValidation("Origin", origin);
        using var unknownResponse = await client.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        Assert.Equal(wrongPasswordBody, await unknownResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain("stack", await unknownResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var adminRoute = await client.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.NotFound, adminRoute.StatusCode);
        await using var auditConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_lastDataDirectory!, "core.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await auditConnection.OpenAsync();
        await using var auditQuery = auditConnection.CreateCommand();
        auditQuery.CommandText = "SELECT group_concat(event_code, ',') FROM core_security_audit WHERE event_code LIKE 'http.%';";
        var auditEvents = Convert.ToString(await auditQuery.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("http.login.succeeded", auditEvents, StringComparison.Ordinal);
        Assert.Contains("http.login.rejected", auditEvents, StringComparison.Ordinal);
        Assert.Contains("http.session.logout", auditEvents, StringComparison.Ordinal);
        Assert.Contains("http.session.rejected", auditEvents, StringComparison.Ordinal);

        await app.StopAsync();
    }

    [Fact]
    public async Task ProcessLoginRateLimitIsConcurrentAndRecoversAfterItsWindow()
    {
        var port = GetAvailableLoopbackPort();
        await using var app = Build(port, loginRateLimit: 2, loginRateWindowSeconds: 2);
        await app.StartAsync();
        using var client = CreateClient(port);
        var requests = Enumerable.Range(0, 8).Select(_ =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new { username = "no-user", password = "not the password at all" })
            };
            request.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{port}");
            return client.SendAsync(request);
        }).ToArray();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(2, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(6, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
            Assert.All(responses.Where(response => response.StatusCode == HttpStatusCode.TooManyRequests), response =>
            {
                Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
                Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        await Task.Delay(TimeSpan.FromSeconds(2.2));
        using var afterWindow = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "no-user", password = "not the password at all" })
        };
        afterWindow.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{port}");
        using var recovered = await client.SendAsync(afterWindow);
        Assert.Equal(HttpStatusCode.Unauthorized, recovered.StatusCode);
        await using (var auditConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_lastDataDirectory!, "core.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            await auditConnection.OpenAsync();
            await using var auditQuery = auditConnection.CreateCommand();
            auditQuery.CommandText = "SELECT COUNT(*) FROM core_security_audit WHERE event_code = 'http.login.rate_limited';";
            Assert.Equal(6L, await auditQuery.ExecuteScalarAsync());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task HttpSessionSurvivesRestartThenExpiresAndRejectsReplay()
    {
        var firstPort = GetAvailableLoopbackPort();
        await using var first = Build(firstPort);
        await first.StartAsync();
        var bootstrap = first.Services.GetRequiredService<ICoreBootstrapService>();
        const string password = "restart-proof-password-2026";
        Assert.Equal(CoreBootstrapResult.Created,
            await bootstrap.CreateInitialAdministratorAsync("restart-owner", password, CancellationToken.None));
        using var firstClient = CreateClient(firstPort);
        var origin = $"https://127.0.0.1:{firstPort}";
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "restart-owner", password })
        };
        login.Headers.TryAddWithoutValidation("Origin", origin);
        using var loginResponse = await firstClient.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<CoreAuthEndpoints.LoginResponse>();
        Assert.NotNull(loginBody);
        var cookies = loginResponse.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = Assert.Single(cookies, value => value.StartsWith("__Secure-HispaniaSession=", StringComparison.Ordinal));
        var csrfCookie = Assert.Single(cookies, value => value.StartsWith("__Secure-HispaniaCsrf=", StringComparison.Ordinal));
        var sessionPair = sessionCookie.Split(';', 2)[0];
        var csrfPair = csrfCookie.Split(';', 2)[0];
        var dataDirectory = _lastDataDirectory!;
        await first.StopAsync();

        var secondPort = GetAvailableLoopbackPort();
        await using var second = Build(secondPort, dataDirectory: dataDirectory);
        await second.StartAsync();
        using var secondClient = CreateClient(secondPort);
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        me.Headers.TryAddWithoutValidation("Cookie", sessionPair);
        using var meResponse = await secondClient.SendAsync(me);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.TryAddWithoutValidation("Cookie", $"{sessionPair}; {csrfPair}");
        logout.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{secondPort}");
        logout.Headers.TryAddWithoutValidation("X-Hispania-CSRF", loginBody.CsrfToken);
        using var logoutResponse = await secondClient.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        replay.Headers.TryAddWithoutValidation("Cookie", sessionPair);
        using var replayResponse = await secondClient.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);

        using var secondLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "restart-owner", password })
        };
        secondLogin.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{secondPort}");
        using var secondLoginResponse = await secondClient.SendAsync(secondLogin);
        Assert.Equal(HttpStatusCode.OK, secondLoginResponse.StatusCode);
        var secondSessionCookie = Assert.Single(secondLoginResponse.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Secure-HispaniaSession=", StringComparison.Ordinal));
        var secondSession = secondSessionCookie.Split(';', 2)[0];
        var token = secondSession.Split('=', 2)[1];
        var tokenBytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
        var tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "core.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var expire = connection.CreateCommand();
            expire.CommandText = "UPDATE core_sessions SET expires_utc = $expired WHERE token_hash = $hash;";
            expire.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            expire.Parameters.Add("$hash", SqliteType.Blob).Value = tokenHash;
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        using var expiredMe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        expiredMe.Headers.TryAddWithoutValidation("Cookie", secondSession);
        using var expiredResponse = await secondClient.SendAsync(expiredMe);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
        CryptographicOperations.ZeroMemory(tokenHash);
        await second.StopAsync();
    }

    [Fact]
    public void HealthRouteIsTheOnlyMappedApplicationEndpoint()
    {
        using var app = Build(GetAvailableLoopbackPort());
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Equal(4, endpoints.Length);
        Assert.Contains(endpoints, endpoint => endpoint is Microsoft.AspNetCore.Routing.RouteEndpoint route && route.RoutePattern.RawText == "/health/live");
        Assert.Contains(endpoints, endpoint => endpoint is Microsoft.AspNetCore.Routing.RouteEndpoint route && route.RoutePattern.RawText == "/api/v1/auth/login");
        Assert.Contains(endpoints, endpoint => endpoint is Microsoft.AspNetCore.Routing.RouteEndpoint route && route.RoutePattern.RawText == "/api/v1/auth/logout");
        Assert.Contains(endpoints, endpoint => endpoint is Microsoft.AspNetCore.Routing.RouteEndpoint route && route.RoutePattern.RawText == "/api/v1/auth/me");
    }

    private WebApplication Build(int port, int loginRateLimit = 20, int loginRateWindowSeconds = 60, string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "hispania-core-host-tests", Guid.NewGuid().ToString("N"));
        _temporaryDataDirectories.Add(dataDirectory);
        _lastDataDirectory = dataDirectory;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(Path.GetTempPath(), $"hispania-core-test-{Guid.NewGuid():N}.pfx");
        var certificatePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(certificatePath, generated.Export(X509ContentType.Pfx, certificatePassword));
        _temporaryCertificateFiles.Add(certificatePath);
        _certificateThumbprints[port] = generated.Thumbprint;
        return CoreHost.Build(new Dictionary<string, string?>
        {
            ["LISTEN_ADDRESS"] = "127.0.0.1",
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SHUTDOWN_TIMEOUT_SECONDS"] = "3",
            ["DATA_DIRECTORY"] = dataDirectory,
            ["HTTPS_CERTIFICATE_PATH"] = certificatePath,
            ["HTTPS_CERTIFICATE_PASSWORD"] = certificatePassword,
            ["LOGIN_RATE_LIMIT"] = loginRateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["LOGIN_RATE_WINDOW_SECONDS"] = loginRateWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private HttpClient CreateClient(int port) => new(new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), _certificateThumbprints[port], StringComparison.OrdinalIgnoreCase)
    }) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };

    public void Dispose()
    {
        foreach (var directory in _temporaryDataDirectories)
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        foreach (var file in _temporaryCertificateFiles)
            if (File.Exists(file)) File.Delete(file);
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<HttpResponseMessage> WaitForHealthAsync(HttpClient client)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            try { return await client.GetAsync("/health/live"); }
            catch (HttpRequestException) { await Task.Delay(50); }
        }
        throw new TimeoutException("Core liveness endpoint did not start in time.");
    }
}
