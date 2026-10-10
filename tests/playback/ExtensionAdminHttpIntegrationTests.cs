using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Audit;
using Hispania.Runtime;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Playback.Tests;

[CollectionDefinition("extension environment", DisableParallelization = true)]
public sealed class ExtensionEnvironment { }

[Collection("extension environment")]
public sealed class ExtensionAdminHttpIntegrationTests
{
    private const string ModuleId = "hispania.extension-demo.greeting";
    private const string Token = "integration-test-admin-token";
    private const string FailureVariable = "HISPANIA_EXTENSION_DEMO_V2_FAIL_START";

    [Fact]
    public async Task AdminApiInstallsActivatesConsumesDeactivatesAndRollsBackACompatibleUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-extension-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldFailure = Environment.GetEnvironmentVariable(FailureVariable);
        try
        {
            await using var v1 = OpenPackage("greeting-1.0.0.hispania", "1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule");
            await using var v2 = OpenPackage("greeting-2.0.0.hispania", "2.0.0", "Hispania.ExtensionDemo.IndependentGreetingModuleV2");

            var logs = new PlatformErrorBoundaryTests.CaptureLogger();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); builder.Logging.AddProvider(logs);
            builder.WebHost.UseTestServer();
            builder.Configuration["Modules:AdminToken"] = Token;
            builder.Services.AddModuleRuntime();
            builder.Services.AddSingleton<IPlatformAuditWriter, InMemoryPlatformAuditWriter>();
            builder.Services.AddSingleton<DistributedWorkCoordinator>();
            builder.Services.AddSingleton<IDistributedWorkCoordinatorV1>(services => services.GetRequiredService<DistributedWorkCoordinator>());
            builder.Services.AddSingleton(serviceProvider => new ModuleExtensionCatalog(root, new Version(1, 0, 0),
                serviceProvider.GetRequiredService<IModuleRuntime>(), serviceProvider.GetRequiredService<IModuleRegistry>(),
                new Dictionary<string, Func<Hispania.ModuleSystem.Contracts.IModule>>(), [ModuleId]));
            await using var app = builder.Build();
            app.MapModuleAdminEndpoints();
            app.MapPublicModuleHealth();
            await app.StartAsync();
            using var client = app.GetTestClient();

            using (var invalid = new HttpRequestMessage(HttpMethod.Post, "/internal/admin/modules/extensions/packages") { Content = new ByteArrayContent([1, 2, 3]) })
            {
                invalid.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                using var rejected = await client.SendAsync(invalid);
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
                Assert.Contains("Module administration request failed.", await rejected.Content.ReadAsStringAsync());
                var audit = Assert.IsType<InMemoryPlatformAuditWriter>(app.Services.GetRequiredService<IPlatformAuditWriter>());
                Assert.Equal("failed", Assert.Single(audit.Records).Result);
                Assert.DoesNotContain(Token, string.Join('\n', logs.Messages));
                Assert.DoesNotContain(root, string.Join('\n', logs.Messages));
            }

            using (var deniedWorkers = await client.GetAsync("/internal/admin/modules/remote-workers"))
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, deniedWorkers.StatusCode);

            using (var denied = await client.PostAsync("/internal/admin/modules/extensions/packages", new ByteArrayContent(v1.ToArray())))
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);

            async Task<HttpResponseMessage> AdminPostAsync(string path, byte[]? package = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = package is null ? new ByteArrayContent([]) : new ByteArrayContent(package)
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                return await client.SendAsync(request);
            }
            async Task<HttpResponseMessage> AdminGetAsync(string path)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                return await client.SendAsync(request);
            }

            using (var authorizedWorker = await client.PostAsync("/internal/admin/modules/remote-workers/worker-http/authorize",
                System.Net.Http.Json.JsonContent.Create(new { certificateThumbprint = new string('2', 64), allowedCapabilities = new[] { new { contractId = "hispania.test.echo", contractVersion = "1.0" } } })))
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, authorizedWorker.StatusCode);
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/internal/admin/modules/remote-workers/worker-http/authorize")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { certificateThumbprint = new string('2', 64), allowedCapabilities = new[] { new { contractId = "hispania.test.echo", contractVersion = "1.0" } } })
            })
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                using var authorizedWorker = await client.SendAsync(request);
                Assert.Equal(System.Net.HttpStatusCode.OK, authorizedWorker.StatusCode);
            }
            using (var workers = await AdminGetAsync("/internal/admin/modules/remote-workers"))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, workers.StatusCode);
                Assert.Contains("worker-http", await workers.Content.ReadAsStringAsync());
            }
            using (var revokedWorker = await AdminDeleteAsync("/internal/admin/modules/remote-workers/worker-http"))
                Assert.Equal(System.Net.HttpStatusCode.OK, revokedWorker.StatusCode);

            using (var installed = await AdminPostAsync("/internal/admin/modules/extensions/packages", v1.ToArray()))
                Assert.Equal(System.Net.HttpStatusCode.Created, installed.StatusCode);
            using (var activated = await AdminPostAsync($"/internal/admin/modules/extensions/{ModuleId}/activate"))
                Assert.Equal(System.Net.HttpStatusCode.OK, activated.StatusCode);
            var runtime = app.Services.GetRequiredService<IModuleRuntime>();
            await runtime.MarkDegradedAsync(ModuleId, "password=closure-secret; /internal/root; Authorization: Bearer closure-secret");
            using (var anonymousHealth = await client.GetAsync("/internal/modules/health")) Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymousHealth.StatusCode);
            using (var publicHealth = await AdminGetAsync("/internal/modules/health"))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, publicHealth.StatusCode);
                var body = await publicHealth.Content.ReadAsStringAsync();
                Assert.Contains(ModuleId, body);
                Assert.DoesNotContain("detail", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("closure-secret", body);
                Assert.DoesNotContain("/internal/root", body);
            }
            Assert.True(await runtime.CheckHealthAsync(ModuleId));
            using (var message = await AdminGetAsync($"/internal/admin/modules/extensions/{ModuleId}/greeting"))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, message.StatusCode);
                Assert.Contains("Hello from an independently implemented extension.", await message.Content.ReadAsStringAsync());
            }

            async Task<HttpResponseMessage> AdminDeleteAsync(string path)
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, path);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                return await client.SendAsync(request);
            }

            using (var installed = await AdminPostAsync("/internal/admin/modules/extensions/packages", v2.ToArray()))
                Assert.Equal(System.Net.HttpStatusCode.Created, installed.StatusCode);
            Environment.SetEnvironmentVariable(FailureVariable, "1");
            using (var failedUpdate = await AdminPostAsync($"/internal/admin/modules/extensions/{ModuleId}/update/2.0.0"))
                Assert.Equal(System.Net.HttpStatusCode.Conflict, failedUpdate.StatusCode);
            using (var oldStillServing = await AdminGetAsync($"/internal/admin/modules/extensions/{ModuleId}/greeting"))
                Assert.Contains("Hello from an independently implemented extension.", await oldStillServing.Content.ReadAsStringAsync());

            Environment.SetEnvironmentVariable(FailureVariable, null);
            using (var updated = await AdminPostAsync($"/internal/admin/modules/extensions/{ModuleId}/update/2.0.0"))
                Assert.Equal(System.Net.HttpStatusCode.OK, updated.StatusCode);
            using (var newMessage = await AdminGetAsync($"/internal/admin/modules/extensions/{ModuleId}/greeting"))
                Assert.Contains("Hello from greeting extension version 2.", await newMessage.Content.ReadAsStringAsync());
            using (var deactivated = await AdminPostAsync($"/internal/admin/modules/extensions/{ModuleId}/deactivate"))
                Assert.Equal(System.Net.HttpStatusCode.OK, deactivated.StatusCode);
            using (var blocked = await AdminGetAsync($"/internal/admin/modules/extensions/{ModuleId}/greeting"))
                Assert.Equal(System.Net.HttpStatusCode.Conflict, blocked.StatusCode);
            using (var reactivated = await AdminPostAsync($"/internal/admin/modules/extensions/{ModuleId}/activate"))
                Assert.Equal(System.Net.HttpStatusCode.OK, reactivated.StatusCode);
            using (var afterReactivate = await AdminGetAsync($"/internal/admin/modules/extensions/{ModuleId}/greeting"))
                Assert.Contains("Hello from greeting extension version 2.", await afterReactivate.Content.ReadAsStringAsync());
            using (var unloaded = await AdminPostAsync($"/internal/admin/modules/extensions/{ModuleId}/unload"))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, unloaded.StatusCode);
                Assert.Contains("true", await unloaded.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(FailureVariable, oldFailure);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static MemoryStream OpenPackage(string packageName, string version, string typeName)
    {
        // Generate from the current independently built module. Cached phase-7 archives may predate
        // compatible additions to the public demo contract and would make this test depend on local artifacts.
        _ = packageName;
        return CreatePackageFixture(version, typeName);
    }

    private static MemoryStream CreatePackageFixture(string version, string typeName)
    {
        var assemblyConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repository = FindRepositoryRoot();
        var assemblyPath = Path.Combine(repository, "modules", "extension-demo", "bin", assemblyConfiguration, "net10.0", "Hispania.ExtensionDemo.dll");
        var payload = File.ReadAllBytes(assemblyPath);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new
        {
            id = ModuleId, name = "Independent greeting extension", version,
            implements = new[] { "hispania.extension-demo.greeting@1.0" },
            provides = new[] { "hispania.extension-demo.greeting@1.0" }, requires = Array.Empty<string>(),
            dependencies = Array.Empty<string>(), optionalDependencies = Array.Empty<string>(), permissions = Array.Empty<string>(),
            minimumHostVersion = "1.0.0", runtime = "dotnet-inprocess-trusted", restartRequiredForUpdate = false,
            entryAssembly = "Hispania.ExtensionDemo.dll", entryType = typeName,
            sharedAssemblies = new[] { "Hispania.ExtensionDemo.Contracts" },
            files = new Dictionary<string, string> { ["Hispania.ExtensionDemo.dll"] = hash }
        };
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("module.json").Open())) writer.Write(JsonSerializer.Serialize(manifest));
            using var output = archive.CreateEntry("Hispania.ExtensionDemo.dll").Open(); output.Write(payload);
        }
        result.Position = 0;
        return result;
    }

    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Hispania.sln"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository root not found from test base directory.");
    }
}
