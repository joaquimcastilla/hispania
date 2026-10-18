using System.IO.Compression;
using System.Security.Cryptography;
using Hispania.Core.Hosting;
using Hispania.Core.Modules;
using Hispania.Core.Persistence;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Slice7.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class Slice7ModuleLifecycleTests
{
    private static readonly System.Text.Json.JsonSerializerOptions PackageJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    [Fact]
    public async Task TrustedFixtureInstallsDisabledRestartsAndUninstallsWithoutPersistingPrivateKey()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var privateKey = signer.ExportPkcs8PrivateKeyPem();
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var moduleHost = FindModuleHost();
        Assert.True(File.Exists(moduleHost));
        var archive = BuildPackage(privateKey, typeof(Slice7FixtureModule));

        await using (var first = Build(directory.Path, publicKey, moduleHost))
        {
            await first.StartAsync();
            var manager = first.Services.GetRequiredService<CoreModuleManager>();
            using var package = new MemoryStream(archive, writable: false);
            Assert.Equal("hispania.slice7.fixture", await manager.InstallAsync(package, CancellationToken.None));
            var installed = Assert.Single(manager.List());
            Assert.True(installed.Trusted);
            Assert.False(installed.Enabled);
            Assert.False(installed.Healthy);
            var signedManifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(
                File.ReadAllText(Path.Combine(directory.Path, "modules", "hispania.slice7.fixture", "1.0.0", "module.json")), PackageJsonOptions);
            Assert.NotNull(signedManifest);
            Assert.Contains("core.modules.manage", signedManifest.Permissions);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "module-staging")));
            Assert.False(File.Exists(Path.Combine(directory.Path, "modules", "private-key.pem")));
            await manager.SetEnabledAsync("hispania.slice7.fixture", true, CancellationToken.None);
            Assert.True(Assert.Single(manager.List()).Healthy);
            await first.StopAsync();
        }

        await using (var restarted = Build(directory.Path, publicKey, moduleHost))
        {
            await restarted.StartAsync();
            var manager = restarted.Services.GetRequiredService<CoreModuleManager>();
            Assert.True(Assert.Single(manager.List()).Healthy);
            await manager.SetEnabledAsync("hispania.slice7.fixture", false, CancellationToken.None);
            Assert.False(Assert.Single(manager.List()).Enabled);
            await manager.RemoveAsync("hispania.slice7.fixture", CancellationToken.None);
            Assert.Empty(manager.List());
            Assert.False(restarted.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }

        CryptographicOperations.ZeroMemory(archive);
    }

    [Fact]
    public async Task FixtureFailureIsRolledBackWithoutStoppingCore()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var privateKey = signer.ExportPkcs8PrivateKeyPem();
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var moduleHost = FindModuleHost();
        await using var app = Build(directory.Path, publicKey, moduleHost);
        await app.StartAsync();
        var manager = app.Services.GetRequiredService<CoreModuleManager>();
        using (var package = new MemoryStream(BuildPackage(privateKey, typeof(Slice7FailingFixtureModule)), writable: false))
            await manager.InstallAsync(package, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => manager.SetEnabledAsync("hispania.slice7.fixture", true, CancellationToken.None));
        var failed = Assert.Single(manager.List());
        Assert.True(failed.Installed);
        Assert.False(failed.Enabled);
        Assert.False(failed.Healthy);
        await manager.RemoveAsync("hispania.slice7.fixture", CancellationToken.None);
        Assert.Empty(manager.List());
        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task UnknownAndRevokedPublishersAreRejectedBeforePackageInstallation()
    {
        using var directory = new TemporaryDirectory();
        using var trusted = RSA.Create(2048);
        using var unknown = RSA.Create(2048);
        var moduleHost = FindModuleHost();
        await using var app = Build(directory.Path, trusted.ExportSubjectPublicKeyInfoPem(), moduleHost, revoked: ["hispania.slice7.test"]);
        await app.StartAsync();
        var manager = app.Services.GetRequiredService<CoreModuleManager>();
        using var unknownPackage = new MemoryStream(BuildPackage(unknown.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule), publisherId: "hispania.slice7.unknown"), writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(unknownPackage, CancellationToken.None));
        using var revokedPackage = new MemoryStream(BuildPackage(trusted.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule)), writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(revokedPackage, CancellationToken.None));
        Assert.Empty(manager.List());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "module-staging")));
    }

    [Fact]
    public async Task InvalidSignatureTamperedPayloadCorruptArchiveAndIncompatibleHostFailClosed()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var moduleHost = FindModuleHost();
        await using var app = Build(directory.Path, signer.ExportSubjectPublicKeyInfoPem(), moduleHost);
        await app.StartAsync();
        var manager = app.Services.GetRequiredService<CoreModuleManager>();
        var invalidSignature = BuildPackage(signer.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule), invalidSignature: true);
        var tamperedPayload = BuildPackage(signer.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule), tamperPayload: true);
        var incompatibleHost = BuildPackage(signer.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule), minimumHostVersion: "2.0.0");
        foreach (var bytes in new[] { invalidSignature, tamperedPayload, incompatibleHost, new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x00 } })
        {
            using var package = new MemoryStream(bytes, writable: false);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(package, CancellationToken.None));
        }
        Assert.Empty(manager.List());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "module-staging")));
        CryptographicOperations.ZeroMemory(invalidSignature);
        CryptographicOperations.ZeroMemory(tamperedPayload);
        CryptographicOperations.ZeroMemory(incompatibleHost);
    }

    private static Microsoft.AspNetCore.Builder.WebApplication Build(string dataDirectory, string publicKey, string moduleHost, string[]? revoked = null) =>
        CoreHost.Build(new Dictionary<string, string?>
        {
            ["DATA_DIRECTORY"] = dataDirectory,
            ["MODULES:TRUSTEDPUBLISHERKEYS:hispania.slice7.test"] = publicKey,
            ["MODULES:REVOKEDPUBLISHERS:0"] = revoked is null ? null : revoked[0],
            ["MODULES:MODULEHOSTPATH"] = moduleHost
        }, httpEnabled: false);

    private static byte[] BuildPackage(string privateKey, Type entryType, string publisherId = "hispania.slice7.test",
        string minimumHostVersion = "1.0.0", bool invalidSignature = false, bool tamperPayload = false)
    {
        var assemblyPath = typeof(Slice7FixtureModule).Assembly.Location;
        var payload = File.ReadAllBytes(assemblyPath);
        var manifest = new ModuleManifest
        {
            PackageFormatVersion = 2,
            PackageVersion = "1.0.0",
            PackageDependencies = [],
            RequiredModuleHostProtocol = 1,
            Id = "hispania.slice7.fixture",
            Name = "HISPANIA Slice 7 Fixture",
            Version = "1.0.0",
            Implements = [],
            MinimumHostVersion = minimumHostVersion,
            MaximumHostVersion = "1.0.0",
            Runtime = "dotnet-outofprocess",
            EntryAssembly = "Hispania.Slice7.Fixture.dll",
            EntryType = entryType.FullName,
            SharedAssemblies = ["Hispania.ModuleSystem.Contracts"],
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Hispania.Slice7.Fixture.dll"] = Convert.ToHexString(SHA256.HashData(payload))
            },
            Permissions = ["core.modules.manage"]
        };
        var signed = ModulePackageSignature.Sign(manifest, publisherId, privateKey);
        if (invalidSignature)
            signed = signed with { Signature = (signed.Signature![0] == 'A' ? "B" : "A") + signed.Signature[1..] };
        if (tamperPayload) payload[0] ^= 0x01;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metadata = zip.CreateEntry("module.json", CompressionLevel.Optimal);
            using (var stream = metadata.Open()) System.Text.Json.JsonSerializer.Serialize(stream, signed, PackageJsonOptions);
            var assembly = zip.CreateEntry("Hispania.Slice7.Fixture.dll", CompressionLevel.Optimal);
            using var payloadStream = assembly.Open();
            payloadStream.Write(payload);
        }
        return output.ToArray();
    }

    private static string FindModuleHost()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "apps", "module-host", "bin", "Release", "net10.0", "win-x64", "Hispania.ModuleHost.dll");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Build output for the approved ModuleHost project was not found.");
    }
}

public sealed class Slice7FailingFixtureModule : Hispania.ModuleSystem.Contracts.IModule
{
    public ModuleDescriptor Descriptor { get; } = new("hispania.slice7.fixture", "HISPANIA Slice 7 Fixture", new Version(1, 0, 0));
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.FromException(new InvalidOperationException("Expected fixture start failure."));
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-slice7-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
