using Hispania.ArchitectureTest.Contracts;
using Hispania.ArchitectureTest.Modules;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Hispania.Media.Domain.Identity;
using Hispania.Library.FFprobe;
using Hispania.Library.Persistence.EFCore;
using Hispania.ExtensionDemo;
using Hispania.ExtensionDemo.Contracts;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hispania.Runtime.Tests;

public sealed class ModuleInfrastructureTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    [Fact]
    public void PublisherSignatureAuthenticatesCanonicalManifestAndHonorsRevocation()
    {
        using var rsa = RSA.Create(2048);
        var manifest = new ModuleManifest
        {
            Id = "publisher.sample", Name = "Sample", Version = "1.0.0", Implements = ["sample.capability@1.0"],
            Provides = ["sample.capability@1.0"], MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["payload.dll"] = new string('a', 64) }
        };
        var signed = ModulePackageSignature.Sign(manifest, "publisher.example", rsa.ExportPkcs8PrivateKeyPem());
        var trusted = new Dictionary<string, string>(StringComparer.Ordinal) { ["publisher.example"] = rsa.ExportSubjectPublicKeyInfoPem() };
        Assert.Equal(PublisherSignatureStatus.Trusted, ModulePackageSignature.Verify(signed, trusted, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(PublisherSignatureStatus.Invalid, ModulePackageSignature.Verify(signed with { Name = "Tampered" }, trusted, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(PublisherSignatureStatus.UnknownPublisher, ModulePackageSignature.Verify(signed, new Dictionary<string, string>(), new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(PublisherSignatureStatus.RevokedPublisher, ModulePackageSignature.Verify(signed, trusted, new HashSet<string>(["publisher.example"], StringComparer.Ordinal)));
    }

    [Fact]
    public async Task ModuleWorkerProtocolFramesAreBoundedAndDetectTruncation()
    {
        var hello = new ModuleWorkerHelloV1("sample.module", "1.2.3", ModuleWorkerProtocolV1.ProtocolId, 1, 1, "launch-token");
        using var stream = new MemoryStream();
        await ModuleWorkerProtocolV1.WriteFrameAsync(stream, hello);
        stream.Position = 0;
        var roundTrip = await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(stream);
        Assert.Equal(hello, roundTrip);

        using var oversized = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ModuleWorkerProtocolV1.WriteFrameAsync(oversized, hello, 8));
        using var truncated = new MemoryStream([3, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(truncated));
        using var invalidLength = new MemoryStream([0, 0, 0, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(invalidLength));
        using var malformedJson = new MemoryStream([1, 0, 0, 0, (byte)'{']);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(malformedJson));
    }

    [Fact]
    public void ModuleWorkerNegotiationAuthenticatesIdentityAndSelectsCommonVersion()
    {
        const string token = "YXV0aGVudGljYXRlZC1sYXVuY2g=";
        var hello = new ModuleWorkerHelloV1("sample.module", "1.2.3", ModuleWorkerProtocolV1.ProtocolId, 1, 2, token);

        var accepted = ModuleWorkerProtocolV1.Negotiate(hello, "sample.module", "1.2.3", token);
        Assert.True(accepted.Accepted);
        Assert.Equal(1, accepted.SelectedProtocolVersion);

        Assert.False(ModuleWorkerProtocolV1.Negotiate(hello with { AuthenticationToken = "not-base64!" }, "sample.module", "1.2.3", token).Accepted);
        Assert.False(ModuleWorkerProtocolV1.Negotiate(hello with { ModuleId = "other.module" }, "sample.module", "1.2.3", token).Accepted);
        Assert.False(ModuleWorkerProtocolV1.Negotiate(hello with { MinimumProtocolVersion = 2, MaximumProtocolVersion = 3 }, "sample.module", "1.2.3", token).Accepted);
    }

    [Fact]
    public void OutOfProcessPoliciesRejectUnboundedOrInconsistentLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutOfProcessRestartPolicy { MaximumRestarts = 100 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutOfProcessRestartPolicy { MaximumBackoff = TimeSpan.FromMilliseconds(10), InitialBackoff = TimeSpan.FromSeconds(1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutOfProcessResourceLimits { MaximumConcurrentRequests = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutOfProcessResourceLimits { MaximumWindowsProcessCount = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutOfProcessResourceLimits { MaximumRequestDuration = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public async Task IndependentlyBuiltGreetingPackageInstallsLoadsUpdatesRollsBackDrainsAndUnloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-package-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousFailureSwitch = Environment.GetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_V2_FAIL_START");
        var moduleId = "hispania.extension-demo.greeting";
        try
        {
            using var firstPackage = CreateGreetingPackage("1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule");
            using var secondPackage = CreateGreetingPackage("2.0.0", "Hispania.ExtensionDemo.IndependentGreetingModuleV2");
            var registry = new ModuleRegistry();
            var runtime = new ModuleRuntime(registry);
            using var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>>(), [moduleId]);

            var installedV1 = await catalog.InstallAsync(firstPackage);
            Assert.Equal(new Version(1, 0, 0), installedV1.Version);
            Assert.Empty(registry.List()); // Installation only reads metadata and writes verified files.
            var discoveredV1 = Assert.Single(catalog.Discover());
            Assert.True(discoveredV1.Compatible);
            Assert.True(discoveredV1.Trusted);
            await catalog.ActivateAsync(moduleId, CancellationToken.None);
            using (var lease = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from an independently implemented extension.", lease.Value.GetMessage());

            using (var heldLease = catalog.Acquire<IExtensionDemoV1>(moduleId))
            {
                var deactivation = catalog.DeactivateAsync(moduleId, CancellationToken.None);
                await Task.Delay(50);
                Assert.False(deactivation.IsCompleted);
                heldLease.Dispose();
                Assert.Equal(ModuleLifecycleState.Stopped, (await deactivation).State);
            }
            Assert.True(await catalog.UnloadAsync(moduleId));
            Assert.Empty(registry.List());
            await catalog.ActivateAsync(moduleId, CancellationToken.None);

            var installedV2 = await catalog.InstallAsync(secondPackage);
            Assert.Equal(new Version(2, 0, 0), installedV2.Version);
            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_V2_FAIL_START", "1");
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpdateAsync(moduleId, "2.0.0"));
            using (var rollbackLease = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from an independently implemented extension.", rollbackLease.Value.GetMessage());

            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_V2_FAIL_START", null);
            var updated = await catalog.UpdateAsync(moduleId, "2.0.0");
            Assert.Equal(new Version(2, 0, 0), Version.Parse(updated.Manifest.Version));
            using (var v2Lease = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from greeting extension version 2.", v2Lease.Value.GetMessage());
            Assert.True(Directory.Exists(installedV1.Directory)); // Prior generation remains available for rollback.
            Assert.DoesNotContain(typeof(Hispania.ExtensionDemo.IndependentGreetingModule).Assembly, registry.List().Select(module => module.GetType().Assembly));

            await catalog.DeactivateAsync(moduleId, CancellationToken.None);
            Assert.True(await catalog.UnloadAsync(moduleId));
            Assert.True(await catalog.UninstallAsync(moduleId, "2.0.0"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_V2_FAIL_START", previousFailureSwitch);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallerRejectsUnavailableContractsAndCorruptPayloadWithoutInstallingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-package-rejections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var registry = new ModuleRegistry();
            var runtime = new ModuleRuntime(registry);
            using var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>>(), ["hispania.extension-demo.greeting"]);
            using var missingContract = CreateGreetingPackage("1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule", ["hispania.missing.contract@1.0"]);
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InstallAsync(missingContract));
            Assert.Empty(registry.List());
            Assert.False(Directory.Exists(Path.Combine(directory, "hispania.extension-demo.greeting")));

            using var corrupted = CreateGreetingPackage("1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule", corruptHash: true);
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InstallAsync(corrupted));
            Assert.Empty(registry.List());
            Assert.False(Directory.Exists(Path.Combine(directory, "hispania.extension-demo.greeting")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PackageInstallationIsInertAndDoesNotGrantExecutionTrust()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-untrusted-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var registry = new ModuleRegistry();
            var runtime = new ModuleRuntime(registry);
            using var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>>(), []);
            using var package = CreateGreetingPackage("1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule");

            var installed = await catalog.InstallAsync(package);

            Assert.True(Directory.Exists(installed.Directory));
            Assert.Empty(registry.List());
            var discovered = Assert.Single(catalog.Discover());
            Assert.False(discovered.Trusted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ActivateAsync(discovered.Manifest.Id, CancellationToken.None));
            Assert.Empty(registry.List());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ActivationRejectsPostInstallPayloadAndManifestSubstitution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-package-substitution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var publisherKey = RSA.Create(2048);
            var publisherId = "hispania.package-owner";
            var registry = new ModuleRegistry();
            var runtime = new ModuleRuntime(registry);
            using var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>>(), ["hispania.extension-demo.greeting"],
                trustedPublisherKeys: new Dictionary<string, string> { [publisherId] = publisherKey.ExportSubjectPublicKeyInfoPem() },
                requirePublisherSignature: true);
            using var package = CreateGreetingPackage("1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule",
                publisherId: publisherId, publisherPrivateKeyPem: publisherKey.ExportPkcs8PrivateKeyPem());
            var installed = await catalog.InstallAsync(package);

            var payloadPath = Path.Combine(installed.Directory, "Hispania.ExtensionDemo.dll");
            var payload = await File.ReadAllBytesAsync(payloadPath);
            var originalPayload = payload.ToArray();
            payload[0] ^= 0xFF;
            await File.WriteAllBytesAsync(payloadPath, payload);
            await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ActivateAsync(installed.Id, CancellationToken.None));
            Assert.Empty(registry.List());

            await File.WriteAllBytesAsync(payloadPath, originalPayload);
            var manifestPath = Path.Combine(installed.Directory, "module.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            manifest["name"] = "Substituted name";
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            _ = catalog.Discover();
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ActivateAsync(installed.Id, CancellationToken.None));
            Assert.Empty(registry.List());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OutOfProcessGreetingRunsThroughPublicContractAndRecoversAfterWorkerCrash()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-oop-greeting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousCrash = Environment.GetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH");
        var previousCrashMarker = Environment.GetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH_MARKER");
        var moduleId = "hispania.extension-demo.greeting";
        ModuleExtensionCatalog? activeCatalog = null;
        try
        {
            var root = FindRepositoryRoot();
            var host = Path.Combine(root, "apps", "module-host", "bin", "Debug", "net10.0", "Hispania.ModuleHost.dll");
            Assert.True(File.Exists(host), $"Expected generic module host at {host}");
            using var publisherKey = RSA.Create(2048);
            var publisherPem = publisherKey.ExportPkcs8PrivateKeyPem();
            var publisherPublicKey = publisherKey.ExportSubjectPublicKeyInfoPem();
            var registry = new ModuleRegistry();
            registry.Register(new ArchitectureTestModuleA());
            var runtime = new ModuleRuntime(registry);
            await runtime.StartModuleAsync("hispania.architecture-test.a");
            activeCatalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>>(), [moduleId], host,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["hispania.experimental.greetings"] = publisherPublicKey },
                requirePublisherSignature: true,
                resourceLimits: new OutOfProcessResourceLimits
                {
                    MaximumRequestDuration = TimeSpan.FromMilliseconds(250),
                    RequireOperatingSystemLimits = !OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("HISPANIA_TEST_WINDOWS_JOB_OBJECT") == "1"
                });
            var catalog = activeCatalog;
            var crashMarker = Path.Combine(directory, "crash-next-operation");
            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH_MARKER", crashMarker);
            using var inProcessPackage = CreateGreetingPackage("1.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule", publisherId: "hispania.experimental.greetings", publisherPrivateKeyPem: publisherPem);
            using var outOfProcessPackage = CreateGreetingPackage("3.0.0", "Hispania.ExtensionDemo.OutOfProcessGreetingModule", runtime: "dotnet-outofprocess", publisherId: "hispania.experimental.greetings", publisherPrivateKeyPem: publisherPem);
            await catalog.InstallAsync(inProcessPackage);
            await catalog.InstallAsync(outOfProcessPackage);
            Assert.All(catalog.Discover(), extension => Assert.Equal(PublisherSignatureStatus.Trusted, extension.PublisherStatus));
            await catalog.UpdateAsync(moduleId, "1.0.0"); // Explicitly select the InProcess generation.
            using (var inProcess = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from an independently implemented extension.", inProcess.Value.GetMessage());
            await catalog.UpdateAsync(moduleId, "3.0.0"); // Swap the same public capability to OutOfProcess.

            var selected = new CapabilityResolver(registry).Resolve<IExtensionDemoV1>("hispania.extension-demo.greeting", new Version(1, 0), moduleId);
            Assert.Equal("Hello from a separately hosted greeting extension.", selected.GetMessage());
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.WaitForMessageAsync(5_000, cancellation.Token));
            await Assert.ThrowsAsync<TimeoutException>(() => selected.WaitForMessageAsync(5_000, CancellationToken.None));

            await File.WriteAllTextAsync(crashMarker, "crash");
            Assert.ThrowsAny<IOException>(() => selected.GetMessage());
            Assert.Equal(ModuleLifecycleState.Degraded, runtime.GetHealth(moduleId).State);
            Assert.Throws<InvalidOperationException>(() => catalog.Acquire<IExtensionDemoV1>(moduleId));

            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH_MARKER", null);
            await catalog.ActivateAsync(moduleId, CancellationToken.None);
            using (var recovered = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from a separately hosted greeting extension.", recovered.Value.GetMessage());
            Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(moduleId).State);
            Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth("hispania.architecture-test.a").State);

            await catalog.UpdateAsync(moduleId, "1.0.0"); // Roll back to the retained InProcess package.
            using (var rolledBack = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from an independently implemented extension.", rolledBack.Value.GetMessage());

            // A newer package signed by an unknown editor must not replace the loaded trusted generation.
            using var unknownPublisherKey = RSA.Create(2048);
            using var unknownPublisherPackage = CreateGreetingPackage("4.0.0", "Hispania.ExtensionDemo.IndependentGreetingModule",
                publisherId: "hispania.unknown.editor", publisherPrivateKeyPem: unknownPublisherKey.ExportPkcs8PrivateKeyPem());
            await catalog.InstallAsync(unknownPublisherPackage);
            _ = catalog.Discover(); // Prefer the newly installed package in discovery while v1 remains loaded.
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpdateAsync(moduleId, "4.0.0"));
            using (var stillTrusted = catalog.Acquire<IExtensionDemoV1>(moduleId))
                Assert.Equal("Hello from an independently implemented extension.", stillTrusted.Value.GetMessage());

            await catalog.RevokePublisherAsync("hispania.experimental.greetings");
            Assert.Equal(ModuleLifecycleState.Stopped, runtime.GetHealth(moduleId).State);
            Assert.Contains(catalog.Discover(), extension => extension.Manifest.Version == "3.0.0" && extension.PublisherStatus == PublisherSignatureStatus.RevokedPublisher);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ActivateAsync(moduleId, CancellationToken.None));
            await catalog.UnloadAsync(moduleId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH", previousCrash);
            Environment.SetEnvironmentVariable("HISPANIA_EXTENSION_DEMO_OOP_CRASH_MARKER", previousCrashMarker);
            if (activeCatalog is not null)
            {
                try { await activeCatalog.UnloadAsync(moduleId); } catch { }
                activeCatalog.Dispose();
            }
            if (Directory.Exists(directory)) { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    [Fact]
    public void ServerProjectDoesNotReferenceTheGreetingImplementation()
    {
        var project = Path.Combine(FindRepositoryRoot(), "apps", "server", "Hispania.Server.csproj");
        Assert.DoesNotContain("modules\\extension-demo\\Hispania.ExtensionDemo.csproj", File.ReadAllText(project), StringComparison.OrdinalIgnoreCase);
    }

    private static MemoryStream CreateGreetingPackage(string version, string entryType, string[]? requiredContracts = null, bool corruptHash = false, string runtime = "dotnet-inprocess-trusted", string? publisherId = null, string? publisherPrivateKeyPem = null)
    {
        var bytes = File.ReadAllBytes(typeof(IndependentGreetingModule).Assembly.Location);
        var contractBytes = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var contractDigest = Convert.ToHexString(SHA256.HashData(contractBytes)).ToLowerInvariant();
        ModuleManifest manifest = new()
        {
            Id = "hispania.extension-demo.greeting", Name = "Independent greeting extension", Version = version,
            Implements = ["hispania.extension-demo.greeting@1.0"], Provides = ["hispania.extension-demo.greeting@1.0"],
            Requires = requiredContracts ?? [], Dependencies = [], OptionalDependencies = [], Permissions = [],
            MinimumHostVersion = "1.0.0", Runtime = runtime, RestartRequiredForUpdate = false,
            EntryAssembly = "Hispania.ExtensionDemo.dll", EntryType = entryType,
            SharedAssemblies = runtime == "dotnet-outofprocess" ? [] : ["Hispania.ExtensionDemo.Contracts"],
            ContractTypes = runtime == "dotnet-outofprocess" ? new Dictionary<string, string>(StringComparer.Ordinal) { ["hispania.extension-demo.greeting@1.0"] = "Hispania.ExtensionDemo.Contracts.IExtensionDemoV1" } : new(StringComparer.Ordinal),
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["Hispania.ExtensionDemo.dll"] = corruptHash ? new string('0', 64) : digest, ["Hispania.ExtensionDemo.Contracts.dll"] = contractDigest }
        };
        if (publisherId is not null && publisherPrivateKeyPem is not null) manifest = ModulePackageSignature.Sign(manifest, publisherId, publisherPrivateKeyPem);
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("module.json");
            using (var writer = new StreamWriter(manifestEntry.Open())) writer.Write(JsonSerializer.Serialize(manifest, WebJsonOptions));
            var assemblyEntry = archive.CreateEntry("Hispania.ExtensionDemo.dll");
            using (var output = assemblyEntry.Open()) output.Write(bytes);
            var contractEntry = archive.CreateEntry("Hispania.ExtensionDemo.Contracts.dll");
            using (var contractOutput = contractEntry.Open()) contractOutput.Write(contractBytes);
        }
        stream.Position = 0;
        return stream;
    }

    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Hispania.sln"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository root not found from test base directory.");
    }

    [Fact]
    public async Task IndependentExtensionIsDiscoveredActivatedAndDeactivatedWithoutLoadingAnAssembly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-extensions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "greeting"));
        await File.WriteAllTextAsync(Path.Combine(directory, "greeting", "module.json"), """
            { "id":"hispania.extension-demo.greeting", "name":"Greeting", "version":"1.0.0",
              "implements":["hispania.extension-demo.greeting@1.0"], "provides":["hispania.extension-demo.greeting@1.0"],
              "requires":[], "dependencies":[], "optionalDependencies":[], "permissions":[],
              "minimumHostVersion":"1.0.0", "runtime":"dotnet-inprocess-trusted" }
            """);
        try
        {
            var registry = new ModuleRegistry();
            var runtime = new ModuleRuntime(registry);
            var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>> { ["hispania.extension-demo.greeting"] = () => new IndependentGreetingModule() });

            var discovered = Assert.Single(catalog.Discover());
            Assert.True(discovered.Compatible);
            Assert.Null(discovered.State);
            var active = await catalog.ActivateAsync(discovered.Manifest.Id, CancellationToken.None);
            Assert.Equal(ModuleLifecycleState.Running, active.State);
            Assert.Equal("Hello from an independently implemented extension.", Assert.IsAssignableFrom<IExtensionDemoV1>(registry.GetById(discovered.Manifest.Id)).GetMessage());
            var inactive = await catalog.DeactivateAsync(discovered.Manifest.Id, CancellationToken.None);
            Assert.Equal(ModuleLifecycleState.Stopped, inactive.State);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncompatibleExtensionIsRejectedWithoutAffectingCompatibleModules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-extensions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "too-new"));
        await File.WriteAllTextAsync(Path.Combine(directory, "too-new", "module.json"), """
            { "id":"hispania.extension-demo.greeting", "name":"Too new", "version":"2.0.0",
              "implements":[], "provides":[], "requires":[], "dependencies":[], "optionalDependencies":[], "permissions":[],
              "minimumHostVersion":"99.0.0", "runtime":"dotnet-inprocess-trusted" }
            """);
        try
        {
            var healthyModule = new TestModule("hispania.test.healthy");
            var registry = new ModuleRegistry();
            registry.Register(healthyModule);
            var runtime = new ModuleRuntime(registry);
            await runtime.StartAsync(CancellationToken.None);
            var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
                new Dictionary<string, Func<IModule>> { ["hispania.extension-demo.greeting"] = () => new IndependentGreetingModule() });
            var incompatible = Assert.Single(catalog.Discover());

            Assert.False(incompatible.Compatible);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ActivateAsync(incompatible.Manifest.Id, CancellationToken.None));
            Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(healthyModule.Descriptor.Id).State);
            Assert.DoesNotContain(registry.List(), module => module.Descriptor.Id == incompatible.Manifest.Id);
            await runtime.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RegistryRegistersModuleA()
    {
        var registry = new ModuleRegistry();
        var module = new ArchitectureTestModuleA();

        registry.Register(module);

        Assert.Same(module, registry.GetById(module.Descriptor.Id));
    }

    [Fact]
    public void RegistryRegistersModuleB()
    {
        var registry = new ModuleRegistry();
        var module = new ArchitectureTestModuleB();

        registry.Register(module);

        Assert.Same(module, registry.GetById(module.Descriptor.Id));
    }

    [Fact]
    public void RegistryRejectsDuplicateModuleIds()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule("hispania.test.duplicate"));

        Assert.Throws<DuplicateModuleIdException>(() => registry.Register(new TestModule("hispania.test.duplicate")));
    }

    [Fact]
    public void ConfigurationSelectsModuleA()
    {
        var resolver = CreateResolver();
        var configuration = CreateConfiguration("hispania.architecture-test.a");

        var service = resolver.Resolve<IArchitectureDemonstrationService>(
            configuration["Modules:ArchitectureDemonstration"]!);

        Assert.Equal("Response from Architecture Test Module A.", service.GetResponse());
    }

    [Fact]
    public void ConfigurationSelectsModuleB()
    {
        var resolver = CreateResolver();
        var configuration = CreateConfiguration("hispania.architecture-test.b");

        var service = resolver.Resolve<IArchitectureDemonstrationService>(
            configuration["Modules:ArchitectureDemonstration"]!);

        Assert.Equal("Response from Architecture Test Module B.", service.GetResponse());
    }

    [Fact]
    public void ResolverFailsForAnUnknownModuleId()
    {
        var resolver = CreateResolver();

        Assert.Throws<ModuleNotFoundException>(() =>
            resolver.Resolve<IArchitectureDemonstrationService>("hispania.architecture-test.unknown"));
    }

    [Fact]
    public void ResolverFailsWhenModuleDoesNotProvideRequestedContract()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule("hispania.test.no-contract"));
        var resolver = new ModuleResolver(registry);

        Assert.Throws<ModuleContractNotProvidedException>(() =>
            resolver.Resolve<IArchitectureDemonstrationService>("hispania.test.no-contract"));
    }

    [Fact]
    public async Task RuntimeTracksTheExpectedLifecycle()
    {
        var module = new TestModule("hispania.test.lifecycle");
        var runtime = CreateRuntime(module);

        Assert.Equal(ModuleLifecycleState.Registered, runtime.GetHealth(module.Descriptor.Id).State);

        await runtime.StartAsync(CancellationToken.None);
        Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.True(runtime.GetHealth(module.Descriptor.Id).IsHealthy);

        await runtime.StopAsync(CancellationToken.None);
        Assert.Equal(ModuleLifecycleState.Stopped, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.False(runtime.GetHealth(module.Descriptor.Id).IsHealthy);
    }

    [Fact]
    public async Task StartupFailureLeavesTheModuleInFailedState()
    {
        var module = new TestModule("hispania.test.startup-failure", failOnStart: true);
        var runtime = CreateRuntime(module);

        await Assert.ThrowsAsync<ModuleStartupException>(() => runtime.StartAsync(CancellationToken.None));

        var health = runtime.GetHealth(module.Descriptor.Id);
        Assert.Equal(ModuleLifecycleState.Failed, health.State);
        Assert.False(health.IsHealthy);
        Assert.NotNull(health.Detail);
    }

    [Fact]
    public async Task RuntimeReportsDegradedAndDrainingStatesAndRecordsTransitions()
    {
        var module = new TestModule("hispania.test.supervised");
        var runtime = CreateRuntime(module);
        var transitions = new List<ModuleHealthTransition>();
        runtime.StateChanged += transitions.Add;

        await runtime.StartAsync(CancellationToken.None);
        await runtime.MarkDegradedAsync(module.Descriptor.Id, "test health failure");
        Assert.Equal(ModuleLifecycleState.Degraded, runtime.GetHealth(module.Descriptor.Id).State);
        await runtime.CheckHealthAsync(module.Descriptor.Id);
        Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(module.Descriptor.Id).State);
        await runtime.DrainModuleAsync(module.Descriptor.Id);
        Assert.Equal(ModuleLifecycleState.Draining, runtime.GetHealth(module.Descriptor.Id).State);
        await runtime.StopModuleAsync(module.Descriptor.Id);

        Assert.Equal(ModuleLifecycleState.Stopped, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.Contains(transitions, transition => transition.To == ModuleLifecycleState.Degraded);
        Assert.Contains(transitions, transition => transition.To == ModuleLifecycleState.Draining);
        Assert.True(runtime.GetHealth(module.Descriptor.Id).Revision >= 6);
    }

    [Fact]
    public async Task RuntimeStartsDeclaredDependenciesBeforeConsumersAndRejectsCycles()
    {
        var startOrder = new List<string>();
        var provider = new OrderedTestModule("hispania.test.provider", startOrder);
        var consumer = new OrderedTestModule("hispania.test.consumer", startOrder,
            [new ModuleDependency(provider.Descriptor.Id, new Version(1, 0, 0))]);
        var registry = new ModuleRegistry();
        registry.Register(consumer);
        registry.Register(provider);
        var runtime = new ModuleRuntime(registry);
        await runtime.StartAsync(CancellationToken.None);
        Assert.Equal([provider.Descriptor.Id, consumer.Descriptor.Id], startOrder);

        var cyclic = new ModuleRegistry();
        var first = new OrderedTestModule("hispania.test.cycle-a", [], [new ModuleDependency("hispania.test.cycle-b", new Version(1, 0))]);
        var second = new OrderedTestModule("hispania.test.cycle-b", [], [new ModuleDependency("hispania.test.cycle-a", new Version(1, 0))]);
        cyclic.Register(first);
        cyclic.Register(second);
        await Assert.ThrowsAsync<ModuleDependencyException>(() => new ModuleRuntime(cyclic).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeUsesExplicitDependencyToSelectAmongMultipleCapabilityProviders()
    {
        var order = new List<string>();
        var chosen = new CapabilityProviderModule("hispania.test.provider-chosen", order);
        var alternative = new CapabilityProviderModule("hispania.test.provider-alternative", order);
        var consumer = new CapabilityConsumerModule("hispania.test.consumer-selected", chosen.Descriptor.Id, order);
        var registry = new ModuleRegistry();
        registry.Register(alternative);
        registry.Register(consumer);
        registry.Register(chosen);

        var runtime = new ModuleRuntime(registry);
        await runtime.StartAsync(CancellationToken.None);

        Assert.Equal(chosen.Descriptor.Id, order[0]);
        Assert.Contains(alternative.Descriptor.Id, order);
        Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(consumer.Descriptor.Id).State);
        await runtime.StopModuleAsync(alternative.Descriptor.Id);
        Assert.Equal(ModuleLifecycleState.Running, runtime.GetHealth(consumer.Descriptor.Id).State);
        await runtime.StopModuleAsync(chosen.Descriptor.Id);
        Assert.Equal(ModuleLifecycleState.Degraded, runtime.GetHealth(consumer.Descriptor.Id).State);
    }

    [Fact]
    public void ConsumerDependsOnlyOnTheArchitectureTestContract()
    {
        var resolver = CreateResolver();
        var consumer = new ContractOnlyConsumer(resolver.Resolve<IArchitectureDemonstrationService>("hispania.architecture-test.a"));

        Assert.Equal("Response from Architecture Test Module A.", consumer.Execute());
    }

    [Fact]
    public void RegistryRuntimeAndResolverHaveSeparateResponsibilities()
    {
        var module = new TestModule("hispania.test.separation");
        var registry = new ModuleRegistry();
        registry.Register(module);
        var runtime = new ModuleRuntime(registry);
        var resolver = new ModuleResolver(registry);

        Assert.Single(registry.List());
        Assert.Equal(ModuleLifecycleState.Registered, runtime.GetHealth(module.Descriptor.Id).State);
        Assert.Throws<ModuleContractNotProvidedException>(() =>
            resolver.Resolve<IArchitectureDemonstrationService>(module.Descriptor.Id));
    }

    [Fact]
    public void CapabilityResolverSelectsOneCompatibleImplementationByContractAndId()
    {
        var catalog = new InMemoryPlannerCatalog();
        var first = new DirectPlayPlanner(catalog);
        var second = new AlternativePlanner();
        var registry = new ModuleRegistry();
        registry.Register(first);
        registry.Register(second);
        var resolver = new CapabilityResolver(registry);

        Assert.Same(first, resolver.Resolve<IPlaybackPlanner>("hispania.playback.planner", new Version(1, 0), first.Descriptor.Id));
        Assert.Same(second, resolver.Resolve<IPlaybackPlanner>("hispania.playback.planner", new Version(1, 0), second.Descriptor.Id));
        Assert.Throws<AmbiguousCapabilityException>(() => resolver.Resolve<IPlaybackPlanner>("hispania.playback.planner", new Version(1, 0)));
        Assert.Throws<CapabilityUnavailableException>(() => resolver.Resolve<IPlaybackPlanner>("hispania.playback.planner", new Version(2, 0)));
    }

    [Fact]
    public void ShadowExecutionReturnsActiveResultAndContainsShadowFailure()
    {
        var comparisons = new List<ShadowComparison<string>>();
        var shadow = new ShadowCapability<string, string>("active", value => "active:" + value, "shadow", _ => throw new InvalidOperationException("shadow failure"), comparisons.Add);

        Assert.Equal("active:request", shadow.Execute("request"));
        var comparison = Assert.Single(comparisons);
        Assert.Equal("active:request", comparison.ActiveResult);
        Assert.Null(comparison.ShadowResult);
        Assert.IsType<InvalidOperationException>(comparison.ShadowFailure);
    }

    [Fact]
    public void PlaybackShadowInvokesBothReadOnlyPlannersAndReturnsActivePlan()
    {
        var id = MediaEntityId.New();
        var active = new CountingPlanner(new PlaybackMediaNotFound(id));
        var shadowVariant = new CountingPlanner(new DirectPlayUnavailable(id));
        Hispania.Playback.Application.ShadowComparison<PlaybackPlanningResult>? comparison = null;
        var shadow = new ShadowPlaybackPlanner("planner-a", active, "planner-b", shadowVariant, result => comparison = result);

        Assert.IsType<PlaybackMediaNotFound>(shadow.Plan(id));
        Assert.Equal(1, active.Calls);
        Assert.Equal(1, shadowVariant.Calls);
        Assert.IsType<PlaybackMediaNotFound>(comparison!.ActiveResult);
        Assert.IsType<DirectPlayUnavailable>(comparison.ShadowResult);
    }

    [Fact]
    public void FunctionalApplicationAssembliesDoNotReferenceOtherImplementations()
    {
        var forbidden = new[] { "Hispania.Metadata.Tmdb", "Hispania.Library.LocalFileSystem", "Hispania.Library.FFprobe", "Hispania.Library.Persistence.Sqlite", "Hispania.Library.Persistence.EFCore", "Hispania.Server" };
        var assemblies = new[] { typeof(DirectPlayPlanner).Assembly, typeof(Hispania.Library.Application.LibraryScanner).Assembly, typeof(Hispania.Metadata.Application.MovieMetadataApplicationService).Assembly };

        foreach (var assembly in assemblies)
        {
            var names = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);
            Assert.DoesNotContain(names, name => forbidden.Any(prohibited => string.Equals(name, prohibited, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void InfrastructureImplementationsDoNotReferenceAdjacentProvidersOrHost()
    {
        AssertNoReference(typeof(FfprobeMediaProbe).Assembly, "Hispania.Library.LocalFileSystem", "Hispania.Server");
        AssertNoReference(typeof(LibraryCatalogDbContext).Assembly, "Microsoft.Data.Sqlite", "Hispania.Library.Persistence.Sqlite", "Hispania.Server");
    }

    [Fact]
    public void ExperimentalExtensionReferencesOnlyPublicContractsAndFrameworkAssemblies()
    {
        var names = typeof(IndependentGreetingModule).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.Contains("Hispania.ModuleSystem.Contracts", names);
        Assert.Contains("Hispania.ExtensionDemo.Contracts", names);
        Assert.DoesNotContain(names, name => name.StartsWith("Hispania.Server", StringComparison.Ordinal)
            || name.StartsWith("Hispania.Library.Persistence", StringComparison.Ordinal)
            || name.StartsWith("Hispania.Metadata.Tmdb", StringComparison.Ordinal)
            || name.StartsWith("Hispania.Playback.Application", StringComparison.Ordinal));
    }

    private static void AssertNoReference(System.Reflection.Assembly assembly, params string[] forbidden)
    {
        var names = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);
        Assert.DoesNotContain(names, name => forbidden.Contains(name, StringComparer.Ordinal));
    }

    private sealed class InMemoryPlannerCatalog : Hispania.Library.Contracts.IMediaCatalogReader
    {
        public Hispania.Library.Contracts.MediaCatalogEntry? GetById(MediaEntityId id) => null;
        public IReadOnlyCollection<Hispania.Library.Contracts.MediaCatalogEntry> GetAll() => [];
    }

    private sealed class AlternativePlanner : IPlaybackPlanner, IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("hispania.playback.alternative-planner", "Alternative planner", new Version(1, 0, 0), [new ModuleCapability("hispania.playback.planner", new Version(1, 0))]);
        public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId) => new DirectPlayUnavailable(mediaEntityId);
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CountingPlanner(PlaybackPlanningResult result) : IPlaybackPlanner
    {
        public int Calls { get; private set; }
        public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId) { Calls++; return result; }
    }

    private static IConfiguration CreateConfiguration(string selectedModuleId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:ArchitectureDemonstration"] = selectedModuleId
            })
            .Build();

    private static ModuleResolver CreateResolver()
    {
        var registry = new ModuleRegistry();
        registry.Register(new ArchitectureTestModuleA());
        registry.Register(new ArchitectureTestModuleB());
        return new ModuleResolver(registry);
    }

    private static ModuleRuntime CreateRuntime(IModule module)
    {
        var registry = new ModuleRegistry();
        registry.Register(module);
        return new ModuleRuntime(registry);
    }

    private sealed class ContractOnlyConsumer(IArchitectureDemonstrationService service)
    {
        public string Execute() => service.GetResponse();
    }

    private sealed class TestModule : IModule
    {
        private readonly bool failOnStart;

        public TestModule(string id, bool failOnStart = false)
        {
            Descriptor = new ModuleDescriptor(id, "Test module", new Version(1, 0, 0));
            this.failOnStart = failOnStart;
        }

        public ModuleDescriptor Descriptor { get; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (failOnStart)
            {
                throw new InvalidOperationException("Expected startup failure.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class OrderedTestModule(string id, List<string> startOrder, IEnumerable<ModuleDependency>? dependencies = null) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, "Ordered module", new Version(1, 0, 0), dependencies: dependencies);
        public ValueTask StartAsync(CancellationToken cancellationToken) { startOrder.Add(id); return ValueTask.CompletedTask; }
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CapabilityProviderModule(string id, List<string> startOrder) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, "Capability provider", new Version(1, 0, 0),
            [new ModuleCapability("hispania.test.selection", new Version(1, 0))]);
        public ValueTask StartAsync(CancellationToken cancellationToken) { startOrder.Add(id); return ValueTask.CompletedTask; }
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CapabilityConsumerModule(string id, string providerId, List<string> startOrder) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, "Capability consumer", new Version(1, 0, 0),
            requires: [new ModuleCapability("hispania.test.selection", new Version(1, 0))],
            dependencies: [new ModuleDependency(providerId, new Version(1, 0, 0))]);
        public ValueTask StartAsync(CancellationToken cancellationToken) { startOrder.Add(id); return ValueTask.CompletedTask; }
        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
