using System.IO.Compression;
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
        using var fixture = new ArtifactProcessTests.HostFixture();
        using (var initial = fixture.Start()) { await fixture.ReadyAsync(initial); await fixture.StopAsync(initial); }
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
        fixture.WriteConfiguration(new { trustedExtensionIds = new[] { manifest.Id }, trustedPublisherKeys = keys });
        var before = File.ReadAllBytes(Path.Combine(root, ".package-state", "catalog.json"));
        var bundle = await InstallationOperations.UpgradeAsync(ArtifactProcessTests.Artifact, fixture.Home);
        Assert.True(File.Exists(Path.Combine(bundle, "extensions", ".package-state", "catalog.json")));
        using (var switched = fixture.Start()) { await fixture.ReadyAsync(switched); await fixture.StopAsync(switched); }
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, ".package-state", "catalog.json")));
        using var current = Catalog(root, new Version(1, 0, 0), keys);
        if (incompatible)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => current.RecoverPackageSetAsync());
            Assert.Equal("HostIncompatible", Assert.Single(current.InspectPackageGraph()).Status);
        }
        else { await current.RecoverPackageSetAsync(); Assert.Equal("Resolved", Assert.Single(current.InspectPackageGraph()).Status); await current.StopManagedPackagesAsync(); }
    }

    private static ModuleExtensionCatalog Catalog(string root, Version host, Dictionary<string, string> keys)
    {
        var registry = new ModuleRegistry();
        return new(root, host, new ModuleRuntime(registry), registry, new Dictionary<string, Func<IModule>>(),
            ["test.package.base"], trustedPublisherKeys: keys, requirePublisherSignature: true);
    }
}
