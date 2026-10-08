using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ExtensionDemo;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class PackageLifecycleIntegrationTests
{
    private const string BaseId = "test.package.base";
    private const string ConsumerId = "test.package.consumer";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ForgedZipManifestLengthIsRejectedBeforeAnyInstallation(bool install)
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-manifest-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "TEST-ONLY-forged-length.hispania");
        try
        {
            using (var output = File.Create(path))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("module.json").Open()))
                writer.Write(new string(' ', 1024 * 1024 + 1) + "{}");
            var bytes = File.ReadAllBytes(path);
            for (var offset = 0; offset <= bytes.Length - 28; offset++)
            {
                var signature = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                if (signature == 0x04034b50) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 22, 4), 2);
                if (signature == 0x02014b50) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), 2);
            }
            File.WriteAllBytes(path, bytes);
            using (var archive = ZipFile.OpenRead(path)) Assert.Equal(2, archive.GetEntry("module.json")!.Length);
            var destination = Path.Combine(root, "installed");
            if (install)
            {
                var installer = new ExtensionPackageInstaller(destination, new Version(1, 0, 0), [], _ => null);
                await using var input = File.OpenRead(path);
                await Assert.ThrowsAnyAsync<JsonException>(() => installer.InstallAsync(input));
            }
            else await Assert.ThrowsAnyAsync<JsonException>(() => PackageArchiveInspector.InspectAsync(path));
            Assert.False(Directory.Exists(destination));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SignedGraphInstallsInertThenEnablesInOrderAndBlocksIndividualUninstall()
    {
        using var fixture = new Fixture();
        var plan = await fixture.Plan();
        Assert.False(Directory.Exists(fixture.Installed));
        Assert.Equal([BaseId, ConsumerId], plan.ActivationOrder);
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        Assert.Empty(fixture.Registry.List());
        Assert.All(fixture.Catalog.InspectPackageGraph(), row =>
        {
            Assert.True(row.Installed); Assert.True(row.Trusted); Assert.True(row.Authorized);
            Assert.False(row.Enabled); Assert.False(row.Healthy);
        });
        var enable = await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>());
        await fixture.Catalog.ApplyPackageSetAsync(enable, new HashSet<string> { BaseId, ConsumerId });
        Assert.Equal("consumer", Message(fixture.Catalog));
        Assert.True(fixture.Runtime.GetHealth(BaseId).IsHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.UninstallAsync(BaseId, "1.0.0"));
        var remove = await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>(), new HashSet<string> { BaseId });
        Assert.False(remove.Resolved);
    }

    [Fact]
    public async Task AuthorizationOfConsumerDoesNotAuthorizeDependency()
    {
        using var fixture = new Fixture(authorized: [ConsumerId]);
        var plan = await fixture.Plan();
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ApplyPackageSetAsync(
            PackageDependencyResolver.Plan(plan.Selected, []), new HashSet<string> { BaseId, ConsumerId }));
        Assert.Empty(fixture.Registry.List());
        Assert.Contains(fixture.Catalog.InspectPackageGraph(), row => row.PackageId == BaseId && row.Status == "UnauthorizedDependency");
    }

    [Theory]
    [InlineData("compatible")][InlineData("missing")][InlineData("conflict")]
    public async Task PlanningPreservesInstalledBytesGraphTrustAuthorizationAndActiveSet(string scenario)
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var next = scenario == "missing"
            ? fixture.Archive(ConsumerId, "2.0.0", typeof(PackageConsumerModule), [new("test.missing", "[1.0.0]")])
            : fixture.Archive(BaseId, scenario == "conflict" ? "2.0.0" : "1.4.0", typeof(PackageBaseModule));
        string Snapshot() => JsonSerializer.Serialize(new
        {
            Files = Directory.GetFiles(fixture.Installed, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path => new
                {
                    Path = Path.GetRelativePath(fixture.Installed, path).Replace('\\', '/'),
                    Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                }).ToArray(),
            Graph = fixture.Catalog.InspectPackageGraph(),
            Active = fixture.Registry.List().Select(module => module.Descriptor.Id).Order(StringComparer.Ordinal).ToArray(),
            PublicKeys = fixture.Keys.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
        }, Json);
        var before = Snapshot();
        var target = scenario == "missing" ? ConsumerId : BaseId;
        var version = scenario == "compatible" ? "1.4.0" : "2.0.0";
        var plan = await fixture.Catalog.PlanPackageSetAsync([next], new Dictionary<string, string> { [target] = $"[{version}]" });
        Assert.Equal(scenario == "compatible", plan.Resolved);
        Assert.Equal(before, Snapshot());
        if (!plan.Resolved)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ApplyPackageSetAsync(plan));
            Assert.Equal(before, Snapshot());
        }
        Assert.Equal("consumer", Message(fixture.Catalog));
    }

    [Fact]
    public async Task HeldCallLeaseUsesConfiguredDrainTimeoutAndRecoversAfterRelease()
    {
        using var fixture = new Fixture(drainTimeout: TimeSpan.FromMilliseconds(200));
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var next = fixture.Archive(BaseId, "1.4.0", typeof(PackageBaseModule));
        var plan = await fixture.Catalog.PlanPackageSetAsync([next], new Dictionary<string, string> { [BaseId] = "[1.4.0]" });
        using (var lease = fixture.Catalog.Acquire<IExtensionDemoV1>(ConsumerId))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.ApplyPackageSetAsync(plan).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("consumer", lease.Value.GetMessage());
        }
        await fixture.Catalog.RecoverPackageSetAsync();
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.Equal("1.0.0", row.Version));
        Assert.Equal("consumer", Message(fixture.Catalog));
    }

    [Fact]
    public async Task FailedHealthRollsBackTheWholeGraph()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var bad = fixture.Archive(ConsumerId, "2.0.0", typeof(UnhealthyPackageConsumerModule), [new(BaseId, "[1.0.0,2.0.0)")]);
        var update = await fixture.Catalog.PlanPackageSetAsync([bad], new Dictionary<string, string> { [ConsumerId] = "[2.0.0]" });
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Catalog.ApplyPackageSetAsync(update));
        Assert.Equal("1.0.0", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == ConsumerId).Version);
        Assert.Equal("consumer", Message(fixture.Catalog));
    }

    [Fact]
    public async Task UpdateWaitsForAdmittedCallsThenUnloadsAndRollbackRestartsPreviousSet()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var lease = fixture.Catalog.Acquire<IExtensionDemoV1>(ConsumerId);
        var admitted = BeginCall(lease);
        var compatible = fixture.Archive(BaseId, "1.4.0", typeof(PackageBaseModule));
        var plan = await fixture.Catalog.PlanPackageSetAsync([compatible], new Dictionary<string, string> { [BaseId] = "[1.4.0]" });
        var update = fixture.Catalog.ApplyPackageSetAsync(plan);
        Assert.False(update.IsCompleted);
        Assert.Equal("consumer", await admitted);
        admitted = null!;
        lease.Dispose(); lease = null!;
        await update;
        Assert.Equal("1.4.0", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == BaseId).Version);
        await fixture.Catalog.RollbackPackageSetAsync();
        Assert.Equal("1.0.0", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == BaseId).Version);
    }

    [Fact]
    public async Task CoordinatedMajorUpdateRequiresCompatibleFinalGraph()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var b = fixture.Archive(BaseId, "2.0.0", typeof(PackageBaseModule));
        Assert.False((await fixture.Catalog.PlanPackageSetAsync([b], new Dictionary<string, string> { [BaseId] = "[2.0.0]" })).Resolved);
        var a = fixture.Archive(ConsumerId, "2.0.0", typeof(PackageConsumerModule), [new(BaseId, "[2.0.0,3.0.0)")]);
        var plan = await fixture.Catalog.PlanPackageSetAsync([a, b], new Dictionary<string, string> { [BaseId] = "[2.0.0]", [ConsumerId] = "[2.0.0]" });
        Assert.True(plan.Resolved);
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.Equal("2.0.0", row.Version));
    }

    [Theory]
    [InlineData(PackageOperationStage.Planned)][InlineData(PackageOperationStage.Staged)][InlineData(PackageOperationStage.Draining)]
    [InlineData(PackageOperationStage.Committed)][InlineData(PackageOperationStage.Activating)][InlineData(PackageOperationStage.Validated)]
    public async Task InjectedJournalBoundaryFailureRestoresKnownGoodSet(PackageOperationStage stage)
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var next = fixture.Archive(BaseId, "1.1.0", typeof(PackageBaseModule));
        var plan = await fixture.Catalog.PlanPackageSetAsync([next], new Dictionary<string, string> { [BaseId] = "[1.1.0]" });
        var fired = false;
        fixture.Catalog.PackageOperationCheckpoint = current => { if (current == stage && !fired) { fired = true; throw new IOException("TEST ONLY injected journal write failure"); } };
        await Assert.ThrowsAsync<IOException>(() => fixture.Catalog.ApplyPackageSetAsync(plan));
        Assert.True(fired);
        Assert.Equal("1.0.0", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == BaseId).Version);
        Assert.True(fixture.Runtime.GetHealth(ConsumerId).IsHealthy);
    }

    [Theory]
    [InlineData("stage-write")][InlineData("catalog-write")][InlineData("journal-write")]
    public async Task InjectedStorageWriteFailurePreservesPreviousBytesAndGraph(string point)
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        var next = fixture.Archive(BaseId, "1.1.0", typeof(PackageBaseModule));
        var plan = await fixture.Catalog.PlanPackageSetAsync([next], new Dictionary<string, string> { [BaseId] = "[1.1.0]" });
        var fired = false;
        fixture.Catalog.PackageStorageCheckpoint = current => { if (current == point && !fired) { fired = true; throw new IOException("TEST ONLY storage write failure"); } };
        await Assert.ThrowsAsync<IOException>(() => fixture.Catalog.ApplyPackageSetAsync(plan));
        Assert.True(fired);
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.Equal("1.0.0", row.Version));
        Assert.True(File.Exists(Path.Combine(fixture.Installed, BaseId, "1.0.0", "Hispania.ExtensionDemo.dll")));
        Assert.Equal("consumer", Message(fixture.Catalog));
    }

    [Fact]
    public async Task StalePlanCannotOverwriteAnAlreadyCommittedOperation()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan());
        var next = fixture.Archive(BaseId, "1.1.0", typeof(PackageBaseModule));
        var first = await fixture.Catalog.PlanPackageSetAsync([next], new Dictionary<string, string> { [BaseId] = "[1.1.0]" });
        var stale = await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>());
        await fixture.Catalog.ApplyPackageSetAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ApplyPackageSetAsync(stale));
        Assert.Equal("1.1.0", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == BaseId).Version);
    }

    [Fact]
    public async Task StagedBytesChangingSincePlanCannotPublishAPartialGraph()
    {
        using var fixture = new Fixture();
        var plan = await fixture.Plan();
        File.AppendAllText(fixture.ConsumerArchive, "TEST ONLY changed bytes");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ApplyPackageSetAsync(plan));
        Assert.Empty(fixture.Catalog.InspectPackageGraph());
        Assert.False(Directory.Exists(Path.Combine(fixture.Installed, BaseId)));
    }

    [Fact]
    public async Task RevocationStopsDependentsAndKnownGoodCannotOverrideCurrentTrust()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>()));
        await fixture.Catalog.RevokePublisherAsync("TEST-ONLY-base");
        Assert.False(fixture.Runtime.GetHealth(ConsumerId).IsHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.RollbackPackageSetAsync());
    }

    [Fact]
    public async Task DifferentPublisherCannotTakeOverAnInstalledIdentity()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan());
        var takeover = fixture.Archive(BaseId, "9.0.0", typeof(PackageBaseModule), publisher: "TEST-ONLY-consumer");
        var plan = await fixture.Catalog.PlanPackageSetAsync([takeover], new Dictionary<string, string> { [BaseId] = "[9.0.0]", [ConsumerId] = "[1.0.0]" });
        Assert.False(plan.Resolved); // Consumer range also protects this set.
        var withoutConsumer = await fixture.Catalog.PlanPackageSetAsync([takeover], new Dictionary<string, string> { [BaseId] = "[9.0.0]" }, new HashSet<string> { ConsumerId });
        Assert.Contains(withoutConsumer.Findings, finding => finding.Code == "PublisherOwnership");
    }

    [Fact]
    public async Task DependencyMetadataIsSignedAndHistoricalNullFieldsAreOmitted()
    {
        using var fixture = new Fixture();
        var candidate = await PackageArchiveInspector.InspectAsync(fixture.ConsumerArchive);
        Assert.Equal(PublisherSignatureStatus.Trusted, ModulePackageSignature.Verify(candidate.Manifest, fixture.Keys, new HashSet<string>()));
        Assert.Equal(PublisherSignatureStatus.Invalid, ModulePackageSignature.Verify(candidate.Manifest with { PackageDependencies = [new(BaseId, "[2.0.0,)")] }, fixture.Keys, new HashSet<string>()));
        var old = candidate.Manifest with { PackageFormatVersion = 1, PackageVersion = null, PackageDependencies = null };
        var text = System.Text.Encoding.UTF8.GetString(ModulePackageSignature.GetSigningBytes(old));
        Assert.DoesNotContain("packageDependencies", text); Assert.DoesNotContain("packageVersion", text);
    }

    [Fact]
    public async Task CorruptCatalogDoesNotResetOrDeleteState()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan());
        var path = Path.Combine(fixture.Installed, ".package-state", "catalog.json");
        File.WriteAllText(path, "{TEST ONLY corrupt");
        Assert.Throws<InvalidDataException>(() => fixture.Catalog.InspectPackageGraph());
        Assert.Equal("{TEST ONLY corrupt", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(PackageOperationStage.Planned)][InlineData(PackageOperationStage.Staged)]
    [InlineData(PackageOperationStage.Committed)][InlineData(PackageOperationStage.Activating)]
    [InlineData(PackageOperationStage.RollingBack)]
    public async Task RealProcessKillRecoversPreviousAuthoritativeGraph(PackageOperationStage stage)
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        await fixture.Catalog.StopManagedPackagesAsync();
        var archive = fixture.Archive(BaseId, "1.1.0", typeof(PackageBaseModule));
        var marker = archive + ".checkpoint"; var keys = archive + ".public-keys.json";
        File.WriteAllText(keys, JsonSerializer.Serialize(fixture.Keys));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.Combine(RepositoryRoot(), "tests", "package-lifecycle-process-host", "bin", configuration, "net10.0", "Hispania.PackageLifecycle.ProcessHost.dll");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { host, fixture.Installed, archive, stage.ToString(), marker, keys }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(marker) && !process.HasExited) await Task.Delay(25, timeout.Token);
            Assert.False(process.HasExited, process.HasExited ? await output + await error : "");
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
            await fixture.Catalog.RecoverPackageSetAsync();
            Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.Equal("1.0.0", row.Version));
            Assert.Equal("consumer", Message(fixture.Catalog));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task OutOfProcessGraphStopsOldHostsAndRollsBackAfterNewHealthFailure()
    {
        using var fixture = new Fixture(outOfProcess: true);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        Assert.Equal("consumer", Message(fixture.Catalog));
        var replacement = fixture.Archive(BaseId, "1.4.0", typeof(PackageBaseModule));
        var plan = await fixture.Catalog.PlanPackageSetAsync([replacement], new Dictionary<string, string> { [BaseId] = "[1.4.0]" });
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        var bad = fixture.Archive(ConsumerId, "2.0.0", typeof(UnhealthyPackageConsumerModule), [new(BaseId, "[1.0.0,2.0.0)")]);
        plan = await fixture.Catalog.PlanPackageSetAsync([bad], new Dictionary<string, string> { [ConsumerId] = "[2.0.0]" });
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Catalog.ApplyPackageSetAsync(plan));
        Assert.Equal("consumer", Message(fixture.Catalog));
        await fixture.Catalog.RollbackPackageSetAsync();
        Assert.Equal("consumer", Message(fixture.Catalog));
    }

    [Fact]
    public async Task DeliberateDowngradeRequiresExplicitConsentAndDoesNotChangeModuleData()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan());
        var next = fixture.Archive(BaseId, "1.4.0", typeof(PackageBaseModule));
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Catalog.PlanPackageSetAsync([next], new Dictionary<string, string> { [BaseId] = "[1.4.0]" }));
        var downgrade = await fixture.Catalog.PlanPackageSetAsync([fixture.BaseArchive], new Dictionary<string, string> { [BaseId] = "[1.0.0]" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ApplyPackageSetAsync(downgrade));
        await fixture.Catalog.ApplyPackageSetAsync(downgrade, allowDowngrade: true);
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.Equal("1.0.0", row.Version));
    }

    [Fact]
    public async Task PublicCapabilityResolverRemainsTheConsumerBoundary()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(), new HashSet<string> { BaseId, ConsumerId });
        Assert.Equal("base", ResolveBaseMessage(fixture.Registry));
        Assert.Equal("consumer", Message(fixture.Catalog));
    }

    [Fact]
    public async Task CleanupRetainsCurrentAndPreviousButRemovesOlderOwnedBytes()
    {
        using var fixture = new Fixture();
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan());
        foreach (var version in new[] { "1.1.0", "1.2.0" })
        {
            var archive = fixture.Archive(BaseId, version, typeof(PackageBaseModule));
            await fixture.Catalog.ApplyPackageSetAsync(await fixture.Catalog.PlanPackageSetAsync([archive], new Dictionary<string, string> { [BaseId] = "[" + version + "]" }));
        }
        await fixture.Catalog.CleanupPackageHistoryAsync();
        Assert.False(Directory.Exists(Path.Combine(fixture.Installed, BaseId, "1.0.0")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Installed, BaseId, "1.1.0")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Installed, BaseId, "1.2.0")));
        await fixture.Catalog.RollbackPackageSetAsync();
        Assert.Equal("1.1.0", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == BaseId).Version);
    }

    [Fact]
    public async Task UnknownPublisherCanBeStoredInertUnderPermissivePolicyButNeverActivated()
    {
        using var fixture = new Fixture(requireSignature: false);
        var unknown = fixture.Archive(BaseId, "1.0.0", typeof(PackageBaseModule), publisher: "TEST-ONLY-unknown");
        var plan = await fixture.Catalog.PlanPackageSetAsync([unknown, fixture.ConsumerArchive], new Dictionary<string, string> { [ConsumerId] = "[1.0.0]" });
        Assert.True(plan.Resolved);
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        var enable = await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ApplyPackageSetAsync(enable, new HashSet<string> { BaseId, ConsumerId }));
        Assert.Empty(fixture.Registry.List());
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.Equal("UntrustedDependency", row.Status));
    }

    [Fact]
    public async Task UnsupportedModuleHostProtocolFailsInPreflight()
    {
        using var fixture = new Fixture(outOfProcess: true);
        var incompatible = fixture.Archive(BaseId, "1.0.0", typeof(PackageBaseModule), requiredProtocol: 2);
        var plan = await fixture.Catalog.PlanPackageSetAsync([incompatible], new Dictionary<string, string> { [BaseId] = "[1.0.0]" });
        Assert.Contains(plan.Findings, finding => finding.Code == "ProtocolIncompatible");
        Assert.False(Directory.Exists(fixture.Installed));
    }

    [Fact]
    public async Task FrozenPhase15PackageEntersManagedSetWithoutChangingBytesOrSignature()
    {
        var repo = RepositoryRoot();
        var archive = Path.Combine(repo, "compatibility", "packages", "0.7", "greeting.hispania");
        var root = Path.Combine(Path.GetTempPath(), "hispania-phase19-historical-" + Guid.NewGuid().ToString("N"));
        var candidate = await PackageArchiveInspector.InspectAsync(archive);
        var key = File.ReadAllText(Path.Combine(repo, "compatibility", "manifests", "0.7", "TEST-ONLY-publisher-public.pem"));
        var registry = new ModuleRegistry(); var runtime = new ModuleRuntime(registry);
        using var catalog = new ModuleExtensionCatalog(root, new Version(1, 0, 0), runtime, registry, new Dictionary<string, Func<IModule>>(),
            [candidate.Id], trustedPublisherKeys: new Dictionary<string, string> { ["TEST-ONLY-baseline-07"] = key }, requirePublisherSignature: true);
        try
        {
            await using (var historical = File.OpenRead(archive)) await catalog.InstallAsync(historical);
            catalog.Discover();
            await catalog.ActivateAsync(candidate.Id, CancellationToken.None);
            Assert.Equal("Hello from an independently implemented extension.", HistoricalMessage(catalog, candidate.Id));
            Assert.True(await catalog.UnloadAsync(candidate.Id));
            var plan = await catalog.PlanPackageSetAsync([archive], new Dictionary<string, string> { [candidate.Id] = "[1.0.0]" });
            Assert.True(plan.Resolved);
            await catalog.ApplyPackageSetAsync(plan, new HashSet<string> { candidate.Id });
            Assert.Equal("Hello from an independently implemented extension.", HistoricalMessage(catalog, candidate.Id));
            await catalog.StopManagedPackagesAsync();
            await catalog.RecoverPackageSetAsync();
            Assert.Equal("Hello from an independently implemented extension.", HistoricalMessage(catalog, candidate.Id));
            await catalog.StopManagedPackagesAsync();
            Assert.Equal(candidate.ArtifactHash, (await PackageArchiveInspector.InspectAsync(archive)).ArtifactHash);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string HistoricalMessage(ModuleExtensionCatalog catalog, string id)
    { using var lease = catalog.Acquire<IExtensionDemoV1>(id); return lease.Value.GetMessage(); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string ResolveBaseMessage(ModuleRegistry registry) => new CapabilityResolver(registry).Resolve<IExtensionDemoV1>(BaseId, new Version(1, 0)).GetMessage();

    [Fact]
    public async Task HistoricalPhase18ReaderRejectsFormatTwoBeforeAnyInstallation()
    {
        var reader = Environment.GetEnvironmentVariable("HISPANIA_PHASE19_OLD_READER") ?? Path.Combine(RepositoryRoot(), ".artifacts", "phase19-gate", "historical-reader", "Hispania.HistoricalPackage.Reader.dll");
        Assert.True(File.Exists(reader), "Run tools/test-phase19-package-lifecycle.ps1 first: the independent historical reader is mandatory.");
        using var fixture = new Fixture();
        var destination = fixture.ConsumerArchive + ".old-host-installed";
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { reader, fixture.ConsumerArchive, destination }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.False(Directory.Exists(destination));
    }

    private static string RepositoryRoot()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "Hispania.sln"))) return parent.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string Message(ModuleExtensionCatalog catalog)
    {
        using var lease = catalog.Acquire<IExtensionDemoV1>(ConsumerId);
        return lease.Value.GetMessage();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<string> BeginCall(ExtensionLease<IExtensionDemoV1> lease) => await lease.Value.WaitForMessageAsync(150, CancellationToken.None);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "hispania-phase19-" + Guid.NewGuid().ToString("N"));
        private readonly RSA rsa = RSA.Create(2048);
        private readonly bool outOfProcess;
        public string Installed => Path.Combine(root, "installed");
        public ModuleRegistry Registry { get; } = new();
        public ModuleRuntime Runtime { get; }
        public ModuleExtensionCatalog Catalog { get; }
        public Dictionary<string, string> Keys { get; }
        public string BaseArchive { get; }
        public string ConsumerArchive { get; }
        public Fixture(string[]? authorized = null, bool outOfProcess = false, bool requireSignature = true, TimeSpan? drainTimeout = null)
        {
            this.outOfProcess = outOfProcess;
            Directory.CreateDirectory(root);
            Keys = new() { ["TEST-ONLY-base"] = rsa.ExportSubjectPublicKeyInfoPem(), ["TEST-ONLY-consumer"] = rsa.ExportSubjectPublicKeyInfoPem() };
            Runtime = new(Registry);
            Catalog = new(Installed, new Version(1, 0, 0), Runtime, Registry, new Dictionary<string, Func<IModule>>(), authorized ?? [BaseId, ConsumerId],
                moduleHostPath: Path.Combine(RepositoryRoot(), "apps", "module-host", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "Hispania.ModuleHost.dll"),
                trustedPublisherKeys: Keys, requirePublisherSignature: requireSignature,
                resourceLimits: drainTimeout is null ? null : new OutOfProcessResourceLimits { MaximumRequestDuration = drainTimeout.Value });
            BaseArchive = Archive(BaseId, "1.0.0", typeof(PackageBaseModule));
            ConsumerArchive = Archive(ConsumerId, "1.0.0", typeof(PackageConsumerModule), [new(BaseId, "[1.0.0,2.0.0)")]);
        }
        public Task<PackageOperationPlan> Plan() => Catalog.PlanPackageSetAsync([BaseArchive, ConsumerArchive], new Dictionary<string, string> { [ConsumerId] = "[1.0.0]" });
        public string Archive(string id, string version, Type type, PackageDependency[]? dependencies = null, string? publisher = null, int? requiredProtocol = null)
        {
            var assembly = File.ReadAllBytes(typeof(PackageBaseModule).Assembly.Location);
            var contract = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location);
            var manifest = new ModuleManifest
            {
                Id = id, Name = id == BaseId ? "TEST ONLY package base" : "TEST ONLY package consumer", Version = "1.0.0",
                PackageVersion = version, PackageFormatVersion = 2, PackageDependencies = dependencies ?? [],
                RequiredModuleHostProtocol = requiredProtocol,
                Implements = [id + "@1.0"], Provides = [id + "@1.0"], MinimumHostVersion = "1.0.0", Runtime = outOfProcess ? "dotnet-outofprocess" : "dotnet-inprocess-trusted",
                EntryAssembly = "Hispania.ExtensionDemo.dll", EntryType = type.FullName, SharedAssemblies = outOfProcess ? [] : ["Hispania.ExtensionDemo.Contracts"],
                ContractTypes = new() { [id + "@1.0"] = typeof(IExtensionDemoV1).FullName! },
                Files = new() { ["Hispania.ExtensionDemo.dll"] = Convert.ToHexString(SHA256.HashData(assembly)), ["Hispania.ExtensionDemo.Contracts.dll"] = Convert.ToHexString(SHA256.HashData(contract)) }
            };
            manifest = ModulePackageSignature.Sign(manifest, publisher ?? (id == BaseId ? "TEST-ONLY-base" : "TEST-ONLY-consumer"), rsa.ExportPkcs8PrivateKeyPem());
            var path = Path.Combine(root, id + "-" + version + "-" + Guid.NewGuid().ToString("N") + ".hispania");
            using var output = File.Create(path);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            using (var metadata = archive.CreateEntry("module.json").Open()) JsonSerializer.Serialize(metadata, manifest, Json);
            using (var entry = archive.CreateEntry("Hispania.ExtensionDemo.dll").Open()) entry.Write(assembly);
            using (var entry = archive.CreateEntry("Hispania.ExtensionDemo.Contracts.dll").Open()) entry.Write(contract);
            return path;
        }
        public void Dispose()
        {
            try
            {
                Catalog.PackageOperationCheckpoint = null;
                Catalog.PackageStorageCheckpoint = null;
                Catalog.StopManagedPackagesAsync().GetAwaiter().GetResult();
                if (File.Exists(Path.Combine(Installed, ".package-state", "catalog.json")))
                {
                    var plan = Catalog.PlanPackageSetAsync([], new Dictionary<string, string>()).GetAwaiter().GetResult();
                    Catalog.ApplyPackageSetAsync(plan, new HashSet<string>()).GetAwaiter().GetResult();
                }
            }
            catch { }
            Catalog.Dispose(); rsa.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
