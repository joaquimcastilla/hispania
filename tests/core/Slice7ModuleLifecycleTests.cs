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
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class Slice7ModuleLifecycleTests
{
    private static readonly System.Text.Json.JsonSerializerOptions PackageJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedRejectionFindingCodes = new(StringComparer.Ordinal)
    {
        "GraphLimit", "InvalidManifest", "AmbiguousArtifact", "MissingDependency", "VersionConflict",
        "CycleOrDepth", "ProtocolIncompatible", "HostIncompatible", "UntrustedDependency", "PublisherOwnership"
    };
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
    public async Task CoreBindsDottedPublisherIdFromPrefixedEnvironmentVariable()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var privateKey = signer.ExportPkcs8PrivateKeyPem();
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var variableName = "HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__hispania.slice7.test";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, publicKey);
        try
        {
            await using var app = CoreHost.Build(new Dictionary<string, string?>
            {
                ["DATA_DIRECTORY"] = directory.Path,
                ["MODULES:MODULEHOSTPATH"] = FindModuleHost()
            }, httpEnabled: false);
            await app.StartAsync();
            var manager = app.Services.GetRequiredService<CoreModuleManager>();
            using var package = new MemoryStream(BuildPackage(privateKey, typeof(Slice7FixtureModule)), writable: false);

            Assert.Equal("hispania.slice7.fixture", await manager.InstallAsync(package, CancellationToken.None));
            Assert.True(Assert.Single(manager.List()).Trusted);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
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
        var trustedPublicKey = trusted.ExportSubjectPublicKeyInfoPem();
        var trustedPrivateKey = trusted.ExportPkcs8PrivateKeyPem();
        var unknownPrivateKey = unknown.ExportPkcs8PrivateKeyPem();
        var moduleHost = FindModuleHost();
        await using var app = Build(directory.Path, trustedPublicKey, moduleHost, revoked: ["hispania.slice7.test"]);
        await app.StartAsync();
        using var capturedLogs = new RejectionLogProvider();
        app.Services.GetRequiredService<ILoggerFactory>().AddProvider(capturedLogs);
        var manager = app.Services.GetRequiredService<CoreModuleManager>();
        using var unknownPackage = new MemoryStream(BuildPackage(unknownPrivateKey, typeof(Slice7FixtureModule), publisherId: "hispania.slice7.unknown"), writable: false);
        var logger = app.Services.GetRequiredService<ILogger<CoreModuleManager>>();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(unknownPackage, CancellationToken.None, logger));
        using var revokedPackage = new MemoryStream(BuildPackage(trustedPrivateKey, typeof(Slice7FixtureModule)), writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(revokedPackage, CancellationToken.None, logger));
        Assert.Empty(manager.List());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "module-staging")));

        var rejections = capturedLogs.Entries.Where(entry => entry.EventId.Id == 30).ToArray();
        Assert.Equal(2, rejections.Length);
        Assert.All(rejections, entry =>
        {
            Assert.Equal("package_plan", entry.Properties["Stage"]);
            Assert.Equal("rejected", entry.Properties["Result"]);
            Assert.Equal("InvalidDataException", entry.Properties["ExceptionType"]);
            var findings = Assert.IsType<string>(entry.Properties["FindingCodes"]).Split(',', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("UntrustedDependency", findings);
            Assert.All(findings, finding => Assert.Contains(finding, AllowedRejectionFindingCodes));
            Assert.Null(entry.Properties["PublisherTrustStatus"]);
            Assert.Matches("^[0-9a-f]{32}$", Assert.IsType<string>(entry.Properties["DiagnosticId"]));
            Assert.False(entry.HasException);
            Assert.DoesNotContain("BEGIN", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(trustedPrivateKey, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(unknownPrivateKey, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(trustedPublicKey, entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PublisherKeyHashIsNotAValidTrustedPublicKeyValue()
    {
        using var signer = RSA.Create(2048);
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var signedManifest = ModulePackageSignature.Sign(new ModuleManifest
        {
            PackageFormatVersion = 2,
            PackageVersion = "1.0.0",
            PackageDependencies = [],
            RequiredModuleHostProtocol = 1,
            Id = "hispania.slice7.fixture",
            Name = "Synthetic trust test",
            Version = "1.0.0",
            Implements = [],
            Provides = [],
            Requires = [],
            OptionalDependencies = [],
            Dependencies = [],
            DependencyMinimumVersions = new(StringComparer.Ordinal),
            Permissions = [],
            MinimumHostVersion = "1.0.0",
            MaximumHostVersion = "1.0.0",
            Runtime = "dotnet-outofprocess",
            EntryAssembly = "fixture.dll",
            EntryType = "Fixture.Entry",
            SharedAssemblies = [],
            ContractTypes = new(StringComparer.Ordinal),
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
        }, "hispania.slice7.test", signer.ExportPkcs8PrivateKeyPem());
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        var trusted = new Dictionary<string, string>(StringComparer.Ordinal) { ["hispania.slice7.test"] = publicKey };
        var digestInPlaceOfKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(publicKey)));
        var incorrect = new Dictionary<string, string>(StringComparer.Ordinal) { ["hispania.slice7.test"] = digestInPlaceOfKey };

        Assert.Equal(PublisherSignatureStatus.Trusted, ModulePackageSignature.Verify(signedManifest, trusted, revoked));
        Assert.Equal(PublisherSignatureStatus.Invalid, ModulePackageSignature.Verify(signedManifest, incorrect, revoked));
    }

    [Fact]
    public async Task CoreRejectsSignedInProcessPackageBeforeInstallation()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var moduleHost = FindModuleHost();
        await using var app = Build(directory.Path, signer.ExportSubjectPublicKeyInfoPem(), moduleHost);
        await app.StartAsync();
        var manager = app.Services.GetRequiredService<CoreModuleManager>();
        using var package = new MemoryStream(BuildPackage(signer.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule), runtime: "dotnet-inprocess-trusted"), writable: false);

        var rejection = await Assert.ThrowsAsync<ModuleRuntimePolicyException>(() => manager.InstallAsync(package, CancellationToken.None));

        Assert.Contains("dotnet-inprocess-trusted", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(manager.List());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "module-staging")));
        Assert.Empty(app.Services.GetRequiredService<IModuleRegistry>().List());
        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task CoreCatalogRejectsLegacyInProcessActivationBeforeModuleLoad()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var moduleHost = FindModuleHost();
        await using var app = Build(directory.Path, signer.ExportSubjectPublicKeyInfoPem(), moduleHost);
        await app.StartAsync();
        var catalog = app.Services.GetRequiredService<ModuleExtensionCatalog>();
        using var package = new MemoryStream(BuildPackage(signer.ExportPkcs8PrivateKeyPem(), typeof(Slice7FixtureModule),
            runtime: "dotnet-inprocess-trusted", packageFormatVersion: 1), writable: false);
        await catalog.InstallAsync(package, CancellationToken.None);

        var rejection = await Assert.ThrowsAsync<ModuleRuntimePolicyException>(() => catalog.ActivateAsync("hispania.slice7.fixture", CancellationToken.None));

        Assert.Contains("dotnet-inprocess-trusted", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(app.Services.GetRequiredService<IModuleRegistry>().List());
        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task CoreRecoveryLeavesPreviouslyPersistedInProcessPackageInactive()
    {
        using var directory = new TemporaryDirectory();
        using var signer = RSA.Create(2048);
        var publicKey = signer.ExportSubjectPublicKeyInfoPem();
        var privateKey = signer.ExportPkcs8PrivateKeyPem();
        var moduleHost = FindModuleHost();
        var modulesDirectory = Path.Combine(directory.Path, "modules");
        var archivePath = Path.Combine(directory.Path, "legacy-inprocess.hispania");
        await File.WriteAllBytesAsync(archivePath, BuildPackage(privateKey, typeof(Slice7FixtureModule), runtime: "dotnet-inprocess-trusted"));
        var registry = new ModuleRegistry();
        using (var legacyCatalog = new ModuleExtensionCatalog(modulesDirectory, new Version(1, 0, 0), new ModuleRuntime(registry), registry,
            new Dictionary<string, Func<IModule>>(), ["hispania.slice7.fixture"], moduleHost,
            new Dictionary<string, string> { ["hispania.slice7.test"] = publicKey }, requirePublisherSignature: true))
        {
            var plan = await legacyCatalog.PlanPackageSetAsync([archivePath], new Dictionary<string, string>
            { ["hispania.slice7.fixture"] = "[1.0.0]" });
            await legacyCatalog.ApplyPackageSetAsync(plan, new HashSet<string>(StringComparer.Ordinal));
        }

        var catalogPath = Path.Combine(modulesDirectory, ".package-state", "catalog.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!;
        state["packages"]![0]!["enabled"] = true;
        await File.WriteAllTextAsync(catalogPath, state.ToJsonString());

        await using var app = Build(directory.Path, publicKey, moduleHost);
        await app.StartAsync();

        Assert.Empty(app.Services.GetRequiredService<IModuleRegistry>().List());
        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
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
        string minimumHostVersion = "1.0.0", bool invalidSignature = false, bool tamperPayload = false,
        string runtime = "dotnet-outofprocess", int packageFormatVersion = 2)
    {
        var assemblyPath = typeof(Slice7FixtureModule).Assembly.Location;
        var payload = File.ReadAllBytes(assemblyPath);
        var manifest = new ModuleManifest
        {
            PackageFormatVersion = packageFormatVersion,
            PackageVersion = packageFormatVersion == 2 ? "1.0.0" : null,
            PackageDependencies = packageFormatVersion == 2 ? [] : null,
            RequiredModuleHostProtocol = packageFormatVersion == 2 ? 1 : null,
            Id = "hispania.slice7.fixture",
            Name = "HISPANIA Slice 7 Fixture",
            Version = "1.0.0",
            Implements = [],
            MinimumHostVersion = minimumHostVersion,
            MaximumHostVersion = "1.0.0",
            Runtime = runtime,
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

    private sealed record CapturedRejection(EventId EventId, string Message, IReadOnlyDictionary<string, object?> Properties, bool HasException);

    private sealed class RejectionLogProvider : ILoggerProvider
    {
        public List<CapturedRejection> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RejectionLogger(Entries);
        public void Dispose() { }

        private sealed class RejectionLogger(List<CapturedRejection> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 30) return;
                var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>();
                entries.Add(new(eventId, formatter(state, exception), properties, exception is not null));
            }
        }
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
