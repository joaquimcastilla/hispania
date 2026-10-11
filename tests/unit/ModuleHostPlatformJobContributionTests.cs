using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleJob.Fixtures;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class ModuleHostPlatformJobContributionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignedModuleHostContributionActivatesExecutesDrainsAndRevokes()
    {
        await VerifyModuleHostFlowAsync("dotnet-outofprocess");
    }

    [Fact]
    public async Task SignedInProcessModuleContributionIsCompatibleAndDrains()
    {
        await VerifyModuleHostFlowAsync("dotnet-inprocess-trusted");
    }

    [Fact]
    public async Task EmptyCoreAndContributionRegistryRemainUsable()
    {
        var jobs = new PlatformJobHandlerRegistryV1();
        Assert.False(jobs.Contains(PlatformJobFixtureModule.JobType));
        await Assert.ThrowsAsync<PlatformJobHandlerUnavailableException>(async () => await jobs.ExecuteAsync(PlatformJobFixtureModule.JobType, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateContributionBatchIsRejectedAtomically()
    {
        var jobs = new PlatformJobHandlerRegistryV1();
        jobs.RegisterModuleBatch("module.one", 1, ["test.one.job"], static (_, _, _) => ValueTask.FromResult<PlatformJobResultMetadataV1?>(null), static () => true);
        Assert.Throws<InvalidOperationException>(() => jobs.RegisterModuleBatch("module.two", 1, ["test.two.job", "test.one.job"], static (_, _, _) => ValueTask.FromResult<PlatformJobResultMetadataV1?>(null), static () => true));
        Assert.False(jobs.Contains("test.two.job"));
        Assert.True(jobs.Contains("test.one.job"));
    }

    [Fact]
    public async Task ContributionWithoutDeclarationStaysCompatibleAndUnregistered()
    {
        using var fixture = new SignedModuleFixture("dotnet-inprocess-trusted");
        await fixture.InstallAndActivateAsync(declareJobs: false);
        Assert.False(fixture.Jobs.Contains(PlatformJobFixtureModule.JobType));
    }

    [Fact]
    public async Task InvalidSignatureAndMissingAllowlistAreRejected()
    {
        using (var invalid = new SignedModuleFixture("dotnet-outofprocess"))
        {
            await invalid.InstallAsync(invalidSignature: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.Catalog.ActivateAsync(PlatformJobFixtureModule.ModuleId, CancellationToken.None));
        }
        using (var untrusted = new SignedModuleFixture("dotnet-inprocess-trusted", allowlisted: false))
        {
            await untrusted.InstallAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => untrusted.Catalog.ActivateAsync(PlatformJobFixtureModule.ModuleId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task UpdateFencesOldGenerationAndPublishesOnlyTheNewGeneration()
    {
        using var fixture = new SignedModuleFixture("dotnet-outofprocess");
        await fixture.InstallAndActivateAsync();
        await fixture.InstallAsync(version: "2.0.0", entryType: typeof(PlatformJobFixtureModuleV2).FullName!);
        await fixture.Catalog.UpdateAsync(PlatformJobFixtureModule.ModuleId, "2.0.0");
        var payload = JsonSerializer.Serialize(new PlatformJobFixtureModule.FixturePayload("generation"), Json);
        var result = await fixture.Jobs.ExecuteAsync(PlatformJobFixtureModule.JobType, payload, CancellationToken.None);
        Assert.Equal("v2:generation", result!.Metadata!["value"]);
        Assert.True(fixture.Jobs.Contains(PlatformJobFixtureModule.JobType));
    }

    [Theory]
    [InlineData("dotnet-outofprocess")]
    [InlineData("dotnet-inprocess-trusted")]
    public async Task DeactivationCancelsAnActiveModuleJobAndWaitsForItsLease(string runtimeKind)
    {
        using var fixture = new SignedModuleFixture(runtimeKind);
        await fixture.InstallAndActivateAsync();
        var marker = Path.Combine(fixture.Root, "job-started.marker");
        var payload = JsonSerializer.Serialize(new PlatformJobFixtureModule.FixturePayload("cancel", 60_000, marker), Json);
        var execution = fixture.Jobs.ExecuteAsync(PlatformJobFixtureModule.JobType, payload, CancellationToken.None).AsTask();
        await WaitForFileAsync(marker);
        await fixture.Catalog.DeactivateAsync(PlatformJobFixtureModule.ModuleId, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.False(fixture.Jobs.Contains(PlatformJobFixtureModule.JobType));
    }

    private static async Task VerifyModuleHostFlowAsync(string runtimeKind)
    {
        using var fixture = new SignedModuleFixture(runtimeKind);
        await fixture.InstallAndActivateAsync();
        Assert.True(fixture.Jobs.Contains(PlatformJobFixtureModule.JobType));
        var payload = JsonSerializer.Serialize(new PlatformJobFixtureModule.FixturePayload("module-result"), Json);
        var result = await fixture.Jobs.ExecuteAsync(PlatformJobFixtureModule.JobType, payload, CancellationToken.None);
        Assert.Equal("module-result", result!.Metadata!["value"]);
        var weakReferences = fixture.CaptureLoadedWeakReferences();
        await fixture.Catalog.DeactivateAsync(PlatformJobFixtureModule.ModuleId, CancellationToken.None);
        Assert.False(fixture.Jobs.Contains(PlatformJobFixtureModule.JobType));
        await Assert.ThrowsAsync<PlatformJobHandlerUnavailableException>(async () => await fixture.Jobs.ExecuteAsync(PlatformJobFixtureModule.JobType, payload, CancellationToken.None));
        await fixture.Catalog.RevokePublisherAsync("TEST-ONLY-module-job-publisher");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ActivateAsync(PlatformJobFixtureModule.ModuleId, CancellationToken.None));
        var unloadResult = await fixture.UnloadModuleAsync();
        Assert.True(unloadResult, $"Unload returned false after draining. ModuleAlive={weakReferences.Module.IsAlive}, ContextAlive={weakReferences.Context.IsAlive}, Registered={fixture.Jobs.Contains(PlatformJobFixtureModule.JobType)}");
        if (runtimeKind == "dotnet-inprocess-trusted")
        {
            Assert.False(weakReferences.Module.IsAlive);
            Assert.False(weakReferences.Context.IsAlive);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
    }

    private sealed class SignedModuleFixture : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        private readonly string publisherKey;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hispania-module-job-" + Guid.NewGuid().ToString("N"));
        public PlatformJobHandlerRegistryV1 Jobs { get; } = new();
        public ModuleExtensionCatalog Catalog { get; }

        public SignedModuleFixture(string runtime, bool allowlisted = true)
        {
            publisherKey = rsa.ExportSubjectPublicKeyInfoPem();
            Directory.CreateDirectory(Root);
            var registry = new ModuleRegistry();
            var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
            while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "Hispania.sln"))) repositoryRoot = repositoryRoot.Parent;
            var moduleHostPath = Path.Combine(repositoryRoot?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "apps", "module-host", "bin", "Debug", "net10.0", "Hispania.ModuleHost.dll");
            Catalog = new ModuleExtensionCatalog(Path.Combine(Root, "extensions"), new Version(1, 0, 0), new ModuleRuntime(registry), registry,
                new Dictionary<string, Func<IModule>>(), allowlisted ? [PlatformJobFixtureModule.ModuleId] : [], moduleHostPath,
                new Dictionary<string, string> { ["TEST-ONLY-module-job-publisher"] = publisherKey }, null,
                true, null, null, null, Jobs);
            Runtime = runtime;
        }

        private string Runtime { get; }

        public async Task InstallAndActivateAsync(bool declareJobs = true)
        {
            await using var package = CreatePackage(Runtime, declareJobs: declareJobs);
            await Catalog.InstallAsync(package);
            await Catalog.ActivateAsync(PlatformJobFixtureModule.ModuleId, CancellationToken.None);
        }

        public async Task InstallAsync(string version = "1.0.0", string? entryType = null, bool invalidSignature = false)
        { await using var package = CreatePackage(Runtime, version, entryType, invalidSignature); await Catalog.InstallAsync(package); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public async Task<bool> UnloadModuleAsync() => await Catalog.UnloadAsync(PlatformJobFixtureModule.ModuleId);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public (WeakReference Module, WeakReference Context) CaptureLoadedWeakReferences()
        {
            var field = typeof(ModuleExtensionCatalog).GetField("loaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var entries = (System.Collections.IDictionary)field.GetValue(Catalog)!;
            var loaded = entries[PlatformJobFixtureModule.ModuleId]!;
            var type = loaded.GetType();
            var module = type.GetProperty("Module")!.GetValue(loaded)!;
            var context = type.GetProperty("Context")!.GetValue(loaded)!;
            return (new WeakReference(module), new WeakReference(context, trackResurrection: true));
        }

        private MemoryStream CreatePackage(string runtime, string version = "1.0.0", string? entryType = null, bool invalidSignature = false, bool declareJobs = true)
        {
            var assemblyPath = typeof(PlatformJobFixtureModule).Assembly.Location;
            var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Hispania.ModuleJob.Fixtures.dll"] = File.ReadAllBytes(assemblyPath)
            };
            if (File.Exists(depsPath)) files["Hispania.ModuleJob.Fixtures.deps.json"] = File.ReadAllBytes(depsPath);
            var capability = $"{PlatformJobFixtureModule.CapabilityId}@1.0";
            var manifest = new ModuleManifest
            {
                Id = PlatformJobFixtureModule.ModuleId, Name = "Test Platform Job module", Version = version,
                Implements = [capability], Provides = [capability], Requires = [], Dependencies = [], OptionalDependencies = [], Permissions = [],
                MinimumHostVersion = "1.0.0", Runtime = runtime, EntryAssembly = "Hispania.ModuleJob.Fixtures.dll",
                EntryType = entryType ?? typeof(PlatformJobFixtureModule).FullName!, SharedAssemblies = ["Hispania.Platform.Contracts"],
                ContractTypes = new Dictionary<string, string>(StringComparer.Ordinal) { [capability] = typeof(IPlatformJobContributionV1).FullName! },
                JobContributions = declareJobs ? [new(PlatformJobFixtureModule.JobType, PlatformJobFixtureModule.CapabilityId, "1.0")] : null,
                Files = files.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)), StringComparer.Ordinal)
            };
            manifest = ModulePackageSignature.Sign(manifest, "TEST-ONLY-module-job-publisher", rsa.ExportPkcs8PrivateKeyPem());
            if (invalidSignature) manifest = manifest with { Signature = new string('0', manifest.Signature!.Length) };
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = archive.CreateEntry("module.json").Open()) JsonSerializer.Serialize(entry, manifest, Json);
                foreach (var pair in files) { using var entry = archive.CreateEntry(pair.Key).Open(); entry.Write(pair.Value); }
            }
            stream.Position = 0;
            return stream;
        }

        public void Dispose()
        {
            Catalog.Dispose();
            rsa.Dispose();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
