using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ExtensionDemo;
using Hispania.ExtensionDemo.Contracts;
using Hispania.Hosting;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Deployment.ProcessTests;

[Trait("Category", "Phase19PackageDistribution")]
public sealed class PackageDistributionUpgradeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OfflineDistributionSwitchPreservesHistoricalFormatGraphAndRevalidatesOptionalHostBounds(bool incompatible)
    {
        var predecessor = Environment.GetEnvironmentVariable("HISPANIA_PHASE18_PREDECESSOR") ?? throw new InvalidOperationException("Mandatory archived predecessor missing.");
        var expectedPredecessorSha = Environment.GetEnvironmentVariable("HISPANIA_PHASE18_PREDECESSOR_SHA") ?? "c3a827a4b828cff37785884e889689af7cc5fce2";
        Assert.Equal(expectedPredecessorSha, ArtifactManifest.Verify(predecessor).GitSha);
        using var fixture = new ArtifactProcessTests.HostFixture(incompatible ? ArtifactProcessTests.Artifact : predecessor);
        var library = Path.Combine(fixture.Home, "library-source");
        Directory.CreateDirectory(library);
        var legacyArgs = new[] { "--home", fixture.Home };
        using (var initial = fixture.Start(incompatible ? null : legacyArgs))
        {
            await fixture.ReadyAsync(initial);
            using var unconfigured = await fixture.Client.GetAsync("/internal/admin/modules/extensions");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unconfigured.StatusCode);
            await fixture.StopAsync(initial);
        }
        using var rsa = RSA.Create(2048);
        var keys = new Dictionary<string, string> { ["TEST-ONLY-distribution-package"] = rsa.ExportSubjectPublicKeyInfoPem() };
        var root = Path.Combine(fixture.Home, "state", "extensions");
        var archive = Path.Combine(fixture.Home, "historical-format.hispania");
        var payloads = new Dictionary<string, byte[]>
        {
            ["Hispania.ExtensionDemo.dll"] = File.ReadAllBytes(typeof(PackageBaseModule).Assembly.Location),
            ["Hispania.ExtensionDemo.Contracts.dll"] = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location)
        };
        // Controlled host-bound fixture; distribution and CLR versions are never invented.
        var manifest = new ModuleManifest
        {
            Id = "test.package.base", Name = "TEST ONLY package base", Version = "1.0.0", PackageFormatVersion = 1,
            Implements = ["test.package.base@1.0"], Provides = ["test.package.base@1.0"],
            MinimumHostVersion = "0.0.0", MaximumHostVersion = incompatible ? "0.9.0" : null,
            Runtime = "dotnet-inprocess-trusted", EntryAssembly = "Hispania.ExtensionDemo.dll",
            EntryType = typeof(PackageBaseModule).FullName, SharedAssemblies = ["Hispania.ExtensionDemo.Contracts"],
            Files = payloads.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)))
        };
        manifest = ModulePackageSignature.Sign(manifest, "TEST-ONLY-distribution-package", rsa.ExportPkcs8PrivateKeyPem());
        using (var output = File.Create(archive))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry("module.json").Open()) JsonSerializer.Serialize(entry, manifest, Json);
            foreach (var pair in payloads) { using var entry = zip.CreateEntry(pair.Key).Open(); entry.Write(pair.Value); }
        }
        using (var previous = Catalog(root, new Version(0, 8, 0), keys))
        {
            var plan = await previous.PlanPackageSetAsync([archive], new Dictionary<string, string> { [manifest.Id] = "[1.0.0]" });
            await previous.ApplyPackageSetAsync(plan, new HashSet<string> { manifest.Id });
            await previous.StopManagedPackagesAsync();
        }
        var adminToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var environment = new Dictionary<string, string> { ["HISPANIA_SECRET_PACKAGE_MODULE_ADMIN"] = adminToken };
        // Keep the RC1 production schema intact; its strict reader predates the moduleAdminToken reference.
        fixture.WriteConfiguration(new { trustedExtensionIds = new[] { manifest.Id }, trustedPublisherKeys = keys });
        var portablePath = Path.Combine(fixture.Home, "state", "portable.json");
        var portable = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(portablePath), Json)!;
        portable["Modules:TrustedExtensionIds:0"] = manifest.Id;
        portable["Modules:TrustedPublisherKeys:TEST-ONLY-distribution-package"] = keys["TEST-ONLY-distribution-package"];
        portable["Modules:RequirePublisherSignature"] = "true";
        File.WriteAllText(portablePath, JsonSerializer.Serialize(portable, Json));
        var historicalConfigurationPath = Path.Combine(fixture.Home, "config", "production.json");
        var historicalConfiguration = File.ReadAllText(historicalConfigurationPath);
        var historicalValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(historicalConfiguration)!;
        historicalValues["trustedExtensionIds"] = JsonSerializer.SerializeToElement(new[] { manifest.Id });
        historicalValues["trustedPublisherKeys"] = JsonSerializer.SerializeToElement(keys);
        File.WriteAllText(historicalConfigurationPath, JsonSerializer.Serialize(historicalValues));
        async Task AssertOperationalAsync()
        {
            using var response = await fixture.Client.GetAsync($"/internal/admin/modules/extensions/{manifest.Id}/greeting");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var greeting = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("base", greeting.GetProperty("message").GetString());
        }
        if (!incompatible)
        {
            var historicalEnvironment = new Dictionary<string, string>
            {
                ["Modules__ExtensionsPath"] = root,
            };
            var preflight = await fixture.CommandAsync(legacyArgs.Append("--check").ToArray(), historicalEnvironment);
            Assert.True(preflight.Code == 0, $"RC1 preflight exit={preflight.Code}; output={preflight.Output}");
            using var historical = fixture.Start(legacyArgs, historicalEnvironment);
            await fixture.ReadyAsync(historical);
            using (var disabled = await fixture.Client.GetAsync("/internal/admin/modules/extensions")) Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
            await fixture.StopAsync(historical);
            fixture.WriteConfiguration(new { trustedExtensionIds = new[] { manifest.Id }, trustedPublisherKeys = keys, moduleAdminToken = "env:PACKAGE_MODULE_ADMIN" });
        }
        var before = File.ReadAllBytes(Path.Combine(root, ".package-state", "catalog.json"));
        var bundle = await InstallationOperations.UpgradeAsync(ArtifactProcessTests.Artifact, fixture.Home);
        Assert.True(File.Exists(Path.Combine(bundle, "extensions", ".package-state", "catalog.json")));
        if (!incompatible)
        {
            using var upgraded = fixture.Start(environment: environment);
            await fixture.ReadyAsync(upgraded);
            fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-test-token");
            using (var rejected = await fixture.Client.GetAsync("/internal/admin/modules/extensions")) Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            fixture.Client.DefaultRequestHeaders.Authorization = null;
            using (var anonymous = await fixture.Client.GetAsync("/internal/admin/modules/extensions")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            using var discovered = await fixture.Client.GetAsync("/internal/admin/modules/extensions");
            Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
            Assert.Contains(manifest.Id, await discovered.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var activation = await fixture.Client.PostAsync($"/internal/admin/modules/extensions/{manifest.Id}/activate", null);
            Assert.Equal(HttpStatusCode.Conflict, activation.StatusCode); // The upgraded host recovered and already activated the committed package graph.
            await AssertOperationalAsync();
            await fixture.StopAsync(upgraded);
            fixture.Client.DefaultRequestHeaders.Authorization = null;
        }
        var password = "TESTONLY!" + Guid.NewGuid().ToString("N");
        fixture.WriteConfiguration(new { trustedExtensionIds = new[] { manifest.Id }, trustedPublisherKeys = keys, bootstrapSecret = "env:PACKAGE_BOOTSTRAP", bootstrapPasswordSecret = "env:PACKAGE_PASSWORD", bootstrapUsername = "package-operator" });
        var bootstrapped = await fixture.CommandAsync(["--home", fixture.Home, "--bootstrap"], new() { ["HISPANIA_SECRET_PACKAGE_BOOTSTRAP"] = Guid.NewGuid().ToString("N"), ["HISPANIA_SECRET_PACKAGE_PASSWORD"] = password });
        Assert.Equal(0, bootstrapped.Code);
        fixture.WriteConfiguration(new { trustedExtensionIds = new[] { manifest.Id }, trustedPublisherKeys = keys });
        using (var switched = fixture.Start())
        {
            await fixture.ReadyAsync(switched);
            using var login = await fixture.Client.PostAsJsonAsync("/api/identity/login", new { username = "package-operator", password });
            login.EnsureSuccessStatusCode(); var session = await login.Content.ReadFromJsonAsync<JsonElement>();
            fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Session", session.GetProperty("sessionToken").GetString());
            if (!incompatible)
            {
                // Production intentionally disables the legacy bearer mutation/greeting surface.
                var health = await fixture.Client.GetFromJsonAsync<JsonElement>("/internal/modules/health");
                Assert.Contains(health.EnumerateArray(), row => row.GetProperty("descriptor").GetProperty("id").GetString() == manifest.Id && row.GetProperty("isHealthy").GetBoolean());
            }
            await fixture.StopAsync(switched);
        }
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, ".package-state", "catalog.json")));
        using var current = Catalog(root, new Version(1, 0, 0), keys);
        if (incompatible)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => current.RecoverPackageSetAsync());
            Assert.Equal("HostIncompatible", Assert.Single(current.InspectPackageGraph()).Status);
        }
        else
        {
            await current.RecoverPackageSetAsync();
            Assert.Equal("Resolved", Assert.Single(current.InspectPackageGraph()).Status);
            using (var lease = current.Acquire<IExtensionDemoV1>(manifest.Id)) Assert.Equal("base", lease.Value.GetMessage());
            await current.StopManagedPackagesAsync();
        }
    }

    private static ModuleExtensionCatalog Catalog(string root, Version host, Dictionary<string, string> keys)
    {
        var registry = new ModuleRegistry();
        return new(root, host, new ModuleRuntime(registry), registry, new Dictionary<string, Func<IModule>>(),
            ["test.package.base"], trustedPublisherKeys: keys, requirePublisherSignature: true);
    }
}
