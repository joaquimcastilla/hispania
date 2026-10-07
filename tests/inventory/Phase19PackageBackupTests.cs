using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ExtensionDemo;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Server;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase19PackageBackupTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OfflineBundleRestoresEnabledGraphAndRejectsMissingDependencyBytes(bool corruptDependency)
    {
        using var installation = new Phase17BackupRestoreTests.Installation();
        using var rsa = RSA.Create(2048);
        var keys = new Dictionary<string, string> { ["TEST-ONLY-package-backup"] = rsa.ExportSubjectPublicKeyInfoPem() };
        var registry = new ModuleRegistry(); var runtime = new ModuleRuntime(registry);
        var root = Path.Combine(installation.Source, "extensions");
        var b = Archive(installation.Root, typeof(PackageBaseModule), [], rsa);
        var a = Archive(installation.Root, typeof(PackageConsumerModule), [new("test.package.base", "[1.0.0,2.0.0)")], rsa);
        using (var catalog = Catalog(root, runtime, registry, keys))
        {
            var plan = await catalog.PlanPackageSetAsync([a, b], new Dictionary<string, string> { ["test.package.consumer"] = "[1.0.0]" });
            await catalog.ApplyPackageSetAsync(plan, new HashSet<string> { "test.package.base", "test.package.consumer" });
            Assert.Equal("consumer", ReadConsumer(catalog));
            await catalog.StopManagedPackagesAsync();
        }
        var portable = new Dictionary<string, string>
        {
            ["Persistence:Provider"] = "sqlite", ["Modules:RequirePublisherSignature"] = "true",
            ["Modules:TrustedPublisherKeys:TEST-ONLY-package-backup"] = keys["TEST-ONLY-package-backup"],
            ["Modules:TrustedExtensionIds:0"] = "test.package.base", ["Modules:TrustedExtensionIds:1"] = "test.package.consumer",
            ["Modules:RevokedPublisherIds:0"] = "TEST-ONLY-unused-revocation"
        };
        File.WriteAllText(Path.Combine(installation.Source, "portable.json"), JsonSerializer.Serialize(portable));
        await BackupInstallation.CreateAsync(installation.Source, installation.Bundle, default);
        Assert.Equal(1, (await BackupInstallation.VerifyAsync(installation.Bundle, default)).BackupFormatGeneration);
        if (corruptDependency)
        {
            File.Delete(Path.Combine(installation.Bundle, "extensions", "test.package.base", "1.0.0", "Hispania.ExtensionDemo.dll"));
            await Assert.ThrowsAnyAsync<Exception>(() => BackupInstallation.RestoreAsync(installation.Bundle, installation.Target, default));
            Assert.False(Directory.Exists(installation.Target));
            return;
        }
        Directory.Delete(installation.Source, recursive: true);
        await BackupInstallation.RestoreAsync(installation.Bundle, installation.Target, default);
        registry = new ModuleRegistry(); runtime = new ModuleRuntime(registry);
        using var restored = Catalog(Path.Combine(installation.Target, "extensions"), runtime, registry, keys);
        await restored.RecoverPackageSetAsync();
        Assert.Equal("consumer", ReadConsumer(restored));
        Assert.Contains("TEST-ONLY-unused-revocation", File.ReadAllText(Path.Combine(installation.Target, "portable.json")));
        await restored.StopManagedPackagesAsync();
    }

    private static ModuleExtensionCatalog Catalog(string root, ModuleRuntime runtime, ModuleRegistry registry, Dictionary<string, string> keys) =>
        new(root, new Version(1, 0, 0), runtime, registry, new Dictionary<string, Func<IModule>>(), ["test.package.base", "test.package.consumer"], trustedPublisherKeys: keys, requirePublisherSignature: true);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string ReadConsumer(ModuleExtensionCatalog catalog)
    { using var lease = catalog.Acquire<IExtensionDemoV1>("test.package.consumer"); return lease.Value.GetMessage(); }

    private static string Archive(string root, Type type, PackageDependency[] dependencies, RSA rsa)
    {
        var module = (IModule)Activator.CreateInstance(type)!;
        var descriptor = module.Descriptor;
        var payloads = new Dictionary<string, byte[]>
        {
            ["Hispania.ExtensionDemo.dll"] = File.ReadAllBytes(type.Assembly.Location),
            ["Hispania.ExtensionDemo.Contracts.dll"] = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location)
        };
        var manifest = new ModuleManifest
        {
            Id = descriptor.Id, Name = descriptor.Name, Version = "1.0.0", PackageVersion = "1.0.0", PackageFormatVersion = 2,
            PackageDependencies = dependencies, Implements = [descriptor.Id + "@1.0"], Provides = [descriptor.Id + "@1.0"],
            MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted", EntryType = type.FullName,
            EntryAssembly = "Hispania.ExtensionDemo.dll", SharedAssemblies = ["Hispania.ExtensionDemo.Contracts"],
            Files = payloads.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)))
        };
        manifest = ModulePackageSignature.Sign(manifest, "TEST-ONLY-package-backup", rsa.ExportPkcs8PrivateKeyPem());
        var path = Path.Combine(root, descriptor.Id + ".hispania");
        using var stream = File.Create(path); using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var entry = archive.CreateEntry("module.json").Open()) JsonSerializer.Serialize(entry, manifest, Json);
        foreach (var pair in payloads) { using var entry = archive.CreateEntry(pair.Key).Open(); entry.Write(pair.Value); }
        return path;
    }
}
