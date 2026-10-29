using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Hispania.Core.Identity;
using Hispania.Core.Modules;
using Hispania.Core.Persistence;
using Hispania.ModuleSystem.Contracts;
using Hispania.Slice7.Fixture;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class CoreStateBackupTests
{
    private const string TestUsername = "backup-test-admin";
    private const string TestPassword = "synthetic backup test password 2026!";
    private static readonly string[] ExpectedArchiveEntries = ["core.db", "manifest.json"];
    private static readonly System.Text.Json.JsonSerializerOptions PackageJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task OfflineBackupRestoresAndReopensCoreOwnedIdentityAndInstanceState()
    {
        using var root = new TemporaryDirectory();
        var sourceData = Path.Combine(root.Path, "source-data");
        var restoredData = Path.Combine(root.Path, "restored-data");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var externalSentinel = Path.Combine(root.Path, "external-sentinel.bin");
        await File.WriteAllBytesAsync(externalSentinel, RandomNumberGenerator.GetBytes(128));
        var sentinelHash = SHA256.HashData(await File.ReadAllBytesAsync(externalSentinel));
        var backupPath = Path.Combine(backupDirectory, "core-state.hispania-backup");
        Guid installationId;

        await using (var source = Build(sourceData))
        {
            await source.StartAsync();
            var persistence = source.Services.GetRequiredService<ICorePersistence>();
            var initialState = await persistence.GetInstanceStateAsync(CancellationToken.None);
            installationId = initialState.InstallationId;
            Assert.Equal(CoreBootstrapResult.Created,
                await source.Services.GetRequiredService<ICoreBootstrapService>()
                    .CreateInitialAdministratorAsync(TestUsername, TestPassword, CancellationToken.None));

            await source.StopAsync();
        }

        var backupResult = await CoreStateBackupService.CreateAsync(GetStorage(sourceData), backupPath, CancellationToken.None);
        Assert.True(backupResult.Length > 0);
        Assert.Matches("^[A-F0-9]{64}$", backupResult.Sha256);
        Assert.True(File.Exists(backupPath));
        if (OperatingSystem.IsWindows()) AssertPrivateBackupAcl(backupPath);

        await CoreStateBackupService.RestoreAsync(GetStorage(restoredData), backupPath, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(restoredData, CoreStorageOptions.DatabaseFileName)));
        Assert.False(Directory.Exists(Path.Combine(restoredData, "modules")));

        await using var reopened = Build(restoredData);
        await reopened.StartAsync();
        var restoredState = await reopened.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.Equal(installationId, restoredState.InstallationId);
        Assert.False(await reopened.Services.GetRequiredService<ICoreBootstrapService>().IsPendingAsync(CancellationToken.None));
        var authentication = await reopened.Services.GetRequiredService<ICoreIdentityService>()
            .AuthenticateAsync(TestUsername, TestPassword, CancellationToken.None);
        Assert.True(authentication.Succeeded);
        Assert.Equal(TestUsername, authentication.Identity!.Username);
        await reopened.StopAsync();

        using var archiveStream = File.OpenRead(backupPath);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        Assert.Equal(ExpectedArchiveEntries, archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
        var manifest = await manifestReader.ReadToEndAsync();
        Assert.Contains("modules package inventory and installed packages", manifest, StringComparison.Ordinal);
        Assert.Contains("module-owned data", manifest, StringComparison.Ordinal);
        Assert.False(manifest.Contains(TestPassword, StringComparison.Ordinal), "Backup manifest contained the synthetic test credential.");
        var sentinelHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(externalSentinel));
        Assert.True(CryptographicOperations.FixedTimeEquals(sentinelHash, sentinelHashAfter));
    }

    [Fact]
    public async Task RestoreCannotRunWhileCoreOwnsTheDataDirectoryLease()
    {
        using var root = new TemporaryDirectory();
        var data = Path.Combine(root.Path, "data");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "active-core.hispania-backup");

        await using var app = Build(data);
        await app.StartAsync();
        var installationId = (await app.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None)).InstallationId;
        var exception = await Assert.ThrowsAsync<CoreStateBackupException>(() =>
            CoreStateBackupService.CreateAsync(GetStorage(data), backupPath, CancellationToken.None));
        Assert.Contains("while a Core process", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(backupPath));
        Assert.Equal(installationId,
            (await app.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None)).InstallationId);
        await app.StopAsync();
    }

    [Fact]
    public async Task SignedTrustedFixtureBackupRestoresModuleInventoryTrustAndLifecycle()
    {
        using var root = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var privateKey = signer.ExportPkcs8PrivateKeyPem();
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var moduleHost = FindModuleHost();
        var sourceData = Path.Combine(root.Path, "source");
        var restoredData = Path.Combine(root.Path, "restored");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "full-state.hispania-backup");
        var packageBytes = BuildPackage(privateKey, typeof(Slice7FixtureModule));

        await using (var source = Build(sourceData, publicKey, moduleHost))
        {
            await source.StartAsync();
            var manager = source.Services.GetRequiredService<CoreModuleManager>();
            using var package = new MemoryStream(packageBytes, writable: false);
            Assert.Equal("hispania.slice7.fixture", await manager.InstallAsync(package, CancellationToken.None));
            Assert.True(Assert.Single(manager.List()).Trusted);
            await manager.SetEnabledAsync("hispania.slice7.fixture", true, CancellationToken.None);
            Assert.True(Assert.Single(manager.List()).Healthy);
            await source.StopAsync();
        }

        await CoreStateBackupService.CreateAsync(GetStorage(sourceData), backupPath, CancellationToken.None);
        using (var stream = File.OpenRead(backupPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var paths = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("modules/hispania.slice7.fixture/1.0.0/module.json", paths);
            Assert.Contains(paths, path => path.StartsWith("modules/.package-state/", StringComparison.Ordinal));
            Assert.DoesNotContain(paths, path => path.Contains("private-key", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, path => path.Contains("trust", StringComparison.OrdinalIgnoreCase));
        }

        await CoreStateBackupService.RestoreAsync(GetStorage(restoredData), backupPath, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(restoredData, "modules", "hispania.slice7.fixture", "1.0.0", "module.json")));

        await using (var reopened = Build(restoredData, publicKey, moduleHost))
        {
            await reopened.StartAsync();
            var manager = reopened.Services.GetRequiredService<CoreModuleManager>();
            var restored = Assert.Single(manager.List());
            Assert.Equal("hispania.slice7.fixture", restored.PackageId);
            Assert.True(restored.Trusted);
            Assert.True(restored.Enabled);
            Assert.True(restored.Healthy);

            using var unknownSigner = RSA.Create(2048);
            using var unknownPackage = new MemoryStream(BuildPackage(unknownSigner.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule)), writable: false);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(unknownPackage, CancellationToken.None));

            using var tamperedPackage = new MemoryStream(BuildPackage(privateKey, typeof(Slice7FixtureModule), tamperPayload: true), writable: false);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(tamperedPackage, CancellationToken.None));
            Assert.True(Assert.Single(manager.List()).Healthy);
            await manager.SetEnabledAsync("hispania.slice7.fixture", false, CancellationToken.None);
            Assert.False(Assert.Single(manager.List()).Enabled);
            await manager.RemoveAsync("hispania.slice7.fixture", CancellationToken.None);
            Assert.Empty(manager.List());
            Assert.False(reopened.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()
                .ApplicationStopping.IsCancellationRequested);
            await reopened.StopAsync();
        }

        CryptographicOperations.ZeroMemory(packageBytes);
        CryptographicOperations.ZeroMemory(System.Text.Encoding.UTF8.GetBytes(privateKey));
    }

    [Fact]
    public async Task FailureAfterModuleStoreReplacementRestoresDatabaseAndPriorInstalledModule()
    {
        using var root = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var privateKey = signer.ExportPkcs8PrivateKeyPem();
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var moduleHost = FindModuleHost();
        var sourceData = Path.Combine(root.Path, "source");
        var targetData = Path.Combine(root.Path, "target");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "module-rollback.hispania-backup");
        var package = BuildPackage(privateKey, typeof(Slice7FixtureModule));

        await using (var source = Build(sourceData, publicKey, moduleHost))
        {
            await source.StartAsync();
            using var sourcePackage = new MemoryStream(package, writable: false);
            await source.Services.GetRequiredService<CoreModuleManager>().InstallAsync(sourcePackage, CancellationToken.None);
            await source.StopAsync();
        }
        await CoreStateBackupService.CreateAsync(GetStorage(sourceData), backupPath, CancellationToken.None);

        Guid targetInstallationId;
        await using (var target = Build(targetData, publicKey, moduleHost))
        {
            await target.StartAsync();
            targetInstallationId = (await target.Services.GetRequiredService<ICorePersistence>()
                .GetInstanceStateAsync(CancellationToken.None)).InstallationId;
            var manager = target.Services.GetRequiredService<CoreModuleManager>();
            using var targetPackage = new MemoryStream(package, writable: false);
            await manager.InstallAsync(targetPackage, CancellationToken.None);
            await manager.SetEnabledAsync("hispania.slice7.fixture", true, CancellationToken.None);
            Assert.True(Assert.Single(manager.List()).Healthy);
            await target.StopAsync();
        }

        var exception = await Assert.ThrowsAsync<CoreStateBackupException>(() =>
            CoreStateBackupService.RestoreWithInjectedPostModuleReplacementFailureAsync(
                GetStorage(targetData), backupPath, CancellationToken.None));
        Assert.Contains("previous database and module store were preserved", exception.Message, StringComparison.Ordinal);

        await using var recovered = Build(targetData, publicKey, moduleHost);
        await recovered.StartAsync();
        Assert.Equal(targetInstallationId, (await recovered.Services.GetRequiredService<ICorePersistence>()
            .GetInstanceStateAsync(CancellationToken.None)).InstallationId);
        var recoveredModule = Assert.Single(recovered.Services.GetRequiredService<CoreModuleManager>().List());
        Assert.True(recoveredModule.Trusted);
        Assert.True(recoveredModule.Enabled);
        Assert.True(recoveredModule.Healthy);
        await recovered.StopAsync();
        CryptographicOperations.ZeroMemory(package);
        CryptographicOperations.ZeroMemory(System.Text.Encoding.UTF8.GetBytes(privateKey));
    }

    [Fact]
    public async Task AlteredBackupIsRejectedBeforeReplacingExistingCoreState()
    {
        using var root = new TemporaryDirectory();
        var sourceData = Path.Combine(root.Path, "source-data");
        var targetData = Path.Combine(root.Path, "target-data");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var validBackup = Path.Combine(backupDirectory, "valid.hispania-backup");
        var alteredBackup = Path.Combine(backupDirectory, "altered.hispania-backup");
        Guid targetId;

        await using (var source = Build(sourceData))
        {
            await source.StartAsync();
            await source.StopAsync();
        }
        await CoreStateBackupService.CreateAsync(GetStorage(sourceData), validBackup, CancellationToken.None);

        await using (var target = Build(targetData))
        {
            await target.StartAsync();
            targetId = (await target.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None)).InstallationId;
            await target.StopAsync();
        }

        await RewriteArchiveAsync(validBackup, alteredBackup, corruptDatabase: true, incompatibleManifest: false);
        var exception = await Assert.ThrowsAsync<CoreStateBackupException>(() =>
            CoreStateBackupService.RestoreAsync(GetStorage(targetData), alteredBackup, CancellationToken.None));
        Assert.Contains("integrity verification failed", exception.Message, StringComparison.Ordinal);

        await using var reopened = Build(targetData);
        await reopened.StartAsync();
        Assert.Equal(targetId,
            (await reopened.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None)).InstallationId);
        await reopened.StopAsync();
    }

    [Fact]
    public async Task IncompatibleBackupFormatAndUnauthorizedOutputPathAreRejected()
    {
        using var root = new TemporaryDirectory();
        var sourceData = Path.Combine(root.Path, "source-data");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var validBackup = Path.Combine(backupDirectory, "valid.hispania-backup");
        var incompatibleBackup = Path.Combine(backupDirectory, "incompatible.hispania-backup");

        await using (var source = Build(sourceData))
        {
            await source.StartAsync();
            await source.StopAsync();
        }
        await CoreStateBackupService.CreateAsync(GetStorage(sourceData), validBackup, CancellationToken.None);
        await RewriteArchiveAsync(validBackup, incompatibleBackup, corruptDatabase: false, incompatibleManifest: true);

        var error = await Assert.ThrowsAsync<CoreStateBackupException>(() =>
            CoreStateBackupService.RestoreAsync(GetStorage(Path.Combine(root.Path, "fresh-data")), incompatibleBackup, CancellationToken.None));
        Assert.Contains("invalid or incompatible", error.Message, StringComparison.Ordinal);

        var insideData = Path.Combine(sourceData, "nested-backup.hispania-backup");
        var pathError = await Assert.ThrowsAsync<CoreStateBackupException>(() =>
            CoreStateBackupService.CreateAsync(GetStorage(sourceData), insideData, CancellationToken.None));
        Assert.Contains("outside the permitted storage boundary", pathError.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(insideData));
    }

    [Fact]
    public async Task FailureAfterDatabaseReplacementRollsBackAndCoreCanReopen()
    {
        using var root = new TemporaryDirectory();
        var sourceData = Path.Combine(root.Path, "source-data");
        var targetData = Path.Combine(root.Path, "target-data");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "rollback.hispania-backup");
        Guid previousInstallationId;

        await using (var source = Build(sourceData))
        {
            await source.StartAsync();
            await source.StopAsync();
        }
        await CoreStateBackupService.CreateAsync(GetStorage(sourceData), backupPath, CancellationToken.None);

        await using (var target = Build(targetData))
        {
            await target.StartAsync();
            previousInstallationId = (await target.Services.GetRequiredService<ICorePersistence>()
                .GetInstanceStateAsync(CancellationToken.None)).InstallationId;
            await target.StopAsync();
        }

        var exception = await Assert.ThrowsAsync<CoreStateBackupException>(() =>
            CoreStateBackupService.RestoreWithInjectedPostDatabaseReplacementFailureAsync(
                GetStorage(targetData), backupPath, CancellationToken.None));
        Assert.Contains("previous database and module store were preserved", exception.Message, StringComparison.Ordinal);

        await using var reopened = Build(targetData);
        await reopened.StartAsync();
        var recoveredState = await reopened.Services.GetRequiredService<ICorePersistence>()
            .GetInstanceStateAsync(CancellationToken.None);
        Assert.Equal(previousInstallationId, recoveredState.InstallationId);
        await reopened.StopAsync();
    }

    [Fact]
    public async Task BackupCliReturnsSuccessWithoutWritingSyntheticCredentialsToOutput()
    {
        using var root = new TemporaryDirectory();
        var dataDirectory = Path.Combine(root.Path, "data");
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "cli-backup.hispania-backup");

        await using (var app = Build(dataDirectory))
        {
            await app.StartAsync();
            Assert.Equal(CoreBootstrapResult.Created,
                await app.Services.GetRequiredService<ICoreBootstrapService>()
                    .CreateInitialAdministratorAsync(TestUsername, TestPassword, CancellationToken.None));
            await app.StopAsync();
        }

        var coreAssembly = Path.Combine(AppContext.BaseDirectory, "Hispania.Core.dll");
        Assert.True(File.Exists(coreAssembly));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root.Path
        };
        startInfo.ArgumentList.Add(coreAssembly);
        startInfo.ArgumentList.Add("backup-core-state");
        startInfo.ArgumentList.Add(backupPath);
        startInfo.Environment["HISPANIA_CORE_DATA_DIRECTORY"] = dataDirectory;
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdoutTask = process!.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = (await stdoutTask) + (await stderrTask);

        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(backupPath));
        Assert.False(output.Contains(TestPassword, StringComparison.Ordinal), "Backup CLI output contained the synthetic test credential.");
    }

    private static WebApplication Build(string dataDirectory, string? trustedPublisherKey = null, string? moduleHostPath = null)
    {
        Directory.CreateDirectory(dataDirectory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(dataDirectory, $"test-{Guid.NewGuid():N}.pfx");
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(certificatePath, generated.Export(X509ContentType.Pfx, password));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var settings = new Dictionary<string, string?>
        {
            ["LISTEN_ADDRESS"] = "127.0.0.1",
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["DATA_DIRECTORY"] = dataDirectory,
            ["HTTPS_CERTIFICATE_PATH"] = certificatePath,
            ["HTTPS_CERTIFICATE_PASSWORD"] = password,
            ["MODULES:TRUSTEDPUBLISHERKEYS:hispania.slice7.test"] = trustedPublisherKey,
            ["MODULES:MODULEHOSTPATH"] = moduleHostPath
        };
        return CoreHost.Build(settings, httpEnabled: false);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivateBackupAcl(string path)
    {
        var acl = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        Assert.True(acl.AreAccessRulesProtected);
        var allowedSids = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(WindowsIdentity.GetCurrent().User!.Value, allowedSids);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, allowedSids);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value, allowedSids);
    }

    private static byte[] BuildPackage(string privateKey, Type entryType, bool tamperPayload = false)
    {
        var payload = File.ReadAllBytes(typeof(Slice7FixtureModule).Assembly.Location);
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
            MinimumHostVersion = "1.0.0",
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
        var signed = ModulePackageSignature.Sign(manifest, "hispania.slice7.test", privateKey);
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
        CryptographicOperations.ZeroMemory(payload);
        return output.ToArray();
    }

    private static string FindModuleHost()
    {
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : null
            : null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidates = runtimeIdentifier is null
                ? new[] { Path.Combine("apps", "module-host", "bin", "Release", "net10.0", "Hispania.ModuleHost.dll") }
                : new[]
            {
                Path.Combine("apps", "module-host", "bin", "Release", "net10.0", runtimeIdentifier, "Hispania.ModuleHost.dll"),
                Path.Combine("apps", "module-host", "bin", "Release", "net10.0", "Hispania.ModuleHost.dll")
            };
            foreach (var relative in candidates)
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("Build output for the approved ModuleHost project was not found.");
    }

    private static CoreStorageOptions GetStorage(string dataDirectory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATA_DIRECTORY"] = dataDirectory
        }).Build();
        return CoreStorageOptions.Read(configuration, AppContext.BaseDirectory);
    }

    private static async Task RewriteArchiveAsync(string sourcePath, string destinationPath, bool corruptDatabase, bool incompatibleManifest)
    {
        using var sourceStream = File.OpenRead(sourcePath);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        await using var destinationStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in source.Entries)
        {
            var newEntry = destination.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
            await using var input = entry.Open();
            await using var output = newEntry.Open();
            using var content = new MemoryStream();
            await input.CopyToAsync(content);
            var bytes = content.ToArray();
            if (entry.FullName == "core.db" && corruptDatabase) bytes[^1] ^= 0x5A;
            if (entry.FullName == "manifest.json" && incompatibleManifest)
            {
                var json = Encoding.UTF8.GetString(bytes).Replace("\"formatVersion\":2", "\"formatVersion\":999", StringComparison.Ordinal);
                bytes = Encoding.UTF8.GetBytes(json);
            }
            await output.WriteAsync(bytes);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-core-backup-tests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
