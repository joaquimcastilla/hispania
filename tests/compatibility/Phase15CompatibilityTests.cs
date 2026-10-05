using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Hispania.Authorization.Application;
using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.ModuleSystem.Contracts;
using Hispania.ExtensionDemo.Contracts;
using Hispania.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Compatibility.Tests;

[Trait("Category", "Phase15Compatibility")]
public sealed class Phase15CompatibilityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void FrozenManifestMembershipAndHashesAreIntact()
    {
        var frozen = File.ReadAllBytes(BaselineCorpus.PathFor("manifests/0.7/frozen-index.json"));
        Assert.Equal("de8431a7f42e4a36979cc372a574cb62094f37399e9ee88a167d17f3a2bb005b", BaselineCorpus.Hash(frozen));
        var errors = BaselineCorpus.Validate(File.ReadAllText(BaselineCorpus.PathFor("baseline-0.7.json")),
            System.Text.Encoding.UTF8.GetString(frozen), path => File.Exists(BaselineCorpus.PathFor(path)) ? File.ReadAllBytes(BaselineCorpus.PathFor(path)) : null);
        Assert.True(errors.Count == 0, string.Join('\n', errors));
        using var manifest = JsonDocument.Parse(File.ReadAllText(BaselineCorpus.PathFor("baseline-0.7.json")));
        var declared = manifest.RootElement.GetProperty("artifacts").EnumerateArray().Select(row => row.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(BaselineCorpus.PathFor(""), "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(BaselineCorpus.PathFor(""), file).Replace('\\', '/');
            var parts = path.Split('/');
            if (parts[0] == "data" || !parts.Contains("0.7") || parts.Any(part => part is "bin" or "obj") || path == "manifests/0.7/frozen-index.json") continue;
            Assert.Contains(path, declared);
        }
    }

    [Fact]
    public void SelectedApiDiffIsDeterministicAndHasNoBreakingChanges()
    {
        var historical = File.ReadAllText(BaselineCorpus.PathFor("api/0.7/selected.txt"));
        var current = SelectedApi.Capture();
        Assert.Equal(current, SelectedApi.Capture());
        var diff = SelectedApi.Diff(historical, current);
        foreach (var diagnostic in diff) output.WriteLine(diagnostic);
        Assert.Equal(diff, SelectedApi.Diff(historical, current));
        Assert.True(!diff.Any(line => line.StartsWith("BREAKING:", StringComparison.Ordinal)), string.Join('\n', diff));
    }

    [Fact]
    public void NegativeApiDiffReportsRemovalSignatureEnumAndAddition()
    {
        var baseline = File.ReadAllText(BaselineCorpus.PathFor("api/0.7/selected.txt"));
        var lines = baseline.Split('\n');
        var member = Array.FindIndex(lines, line => line.Contains("GetUserAsync", StringComparison.Ordinal));
        Assert.True(member > 0);
        var changed = string.Join('\n', lines.Where((_, index) => index != member));
        Assert.Contains(SelectedApi.Diff(baseline, changed), line => line.StartsWith("BREAKING:", StringComparison.Ordinal) && line.Contains("GetUserAsync", StringComparison.Ordinal));
        Assert.Contains(SelectedApi.Diff(baseline, baseline.Replace("GetUserAsync", "ChangedReturnSignature", StringComparison.Ordinal)), line => line.StartsWith("BREAKING:", StringComparison.Ordinal));
        Assert.Contains(SelectedApi.Diff("TYPE Test.Enum | base=System.Enum\nField Int32 Enabled | value=0\n", "TYPE Test.Enum | base=System.Enum\nField Int32 Enabled | value=1\n"), line => line.StartsWith("BREAKING:", StringComparison.Ordinal));
        Assert.Contains(SelectedApi.Diff(baseline, baseline + "Method Added()\n"), line => line.StartsWith("ADDITIVE:", StringComparison.Ordinal));
        Assert.DoesNotContain(SelectedApi.Diff(baseline, baseline + "Method Added()\n"), line => line.StartsWith("BREAKING:", StringComparison.Ordinal));
        const string publicType = "TYPE Test.Surface | base=System.Object\nMethod System.Int32 Read() | public=True\n";
        Assert.Contains(SelectedApi.Diff(publicType, ""), line => line.StartsWith("BREAKING:", StringComparison.Ordinal) && line.Contains("TYPE Test.Surface", StringComparison.Ordinal));
        Assert.Contains(SelectedApi.Diff(publicType, publicType.Replace("public=True", "public=False", StringComparison.Ordinal)), line => line.StartsWith("BREAKING:", StringComparison.Ordinal));
        Assert.Contains(SelectedApi.Diff(publicType, publicType.Replace("System.Int32", "System.String", StringComparison.Ordinal)), line => line.StartsWith("BREAKING:", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeCorpusRejectsMissingChangedAndOmittedArtifactsAndMalformedManifest()
    {
        var manifest = File.ReadAllText(BaselineCorpus.PathFor("baseline-0.7.json"));
        var frozen = File.ReadAllText(BaselineCorpus.PathFor("manifests/0.7/frozen-index.json"));
        Assert.Contains(BaselineCorpus.Validate(manifest, frozen, _ => null), line => line.Contains("missing fixture", StringComparison.Ordinal));
        Assert.Contains(BaselineCorpus.Validate(manifest, frozen, _ => [0]), line => line.Contains("hash changed", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(manifest);
        var omitted = JsonSerializer.Serialize(new { baseline = "0.7", sourceSha = BaselineCorpus.SourceSha, artifacts = document.RootElement.GetProperty("artifacts").EnumerateArray().Skip(1).ToArray() });
        Assert.Contains(BaselineCorpus.Validate(omitted, frozen, _ => null), line => line.Contains("entry removed/changed", StringComparison.Ordinal));
        var rehashed = System.Text.Json.Nodes.JsonNode.Parse(manifest)!;
        rehashed["artifacts"]![0]!["sha256"] = BaselineCorpus.Hash([0]);
        Assert.Contains(BaselineCorpus.Validate(rehashed.ToJsonString(), frozen, _ => [0]), line => line.Contains("entry removed/changed", StringComparison.Ordinal));
        Assert.ThrowsAny<JsonException>(() => BaselineCorpus.Validate("{", frozen, _ => null));
    }

    [Fact]
    public async Task HistoricalIdentityAndAuthorizationBinaryConsumerRunsWithoutRecompilation()
    {
        var path = BaselineCorpus.PathFor("clients/0.7/identity/Hispania.Compatibility.Consumer07.dll");
        var before = BaselineCorpus.Hash(File.ReadAllBytes(path));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        Assert.Contains(assembly.GetReferencedAssemblies(), reference => reference.Name == "Hispania.Platform.Contracts");
        await ExerciseConsumerAsync(assembly);
        Assert.Equal(before, BaselineCorpus.Hash(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task FrozenSourceConsumerCompilesAndExecutesAgainstCurrentContracts()
    {
        using var directory = new TemporaryDirectory();
        // Build only the frozen consumer source; the binary test above never invokes a compiler.
        var project = Path.Combine(directory.Path, "Consumer.csproj");
        File.Copy(BaselineCorpus.PathFor("clients/0.7/identity/Consumer.csproj"), project);
        File.Copy(BaselineCorpus.PathFor("clients/0.7/identity/Consumer.cs"), Path.Combine(directory.Path, "Consumer.cs"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "build", project, "--nologo", "-p:PlatformContractsPath=" + typeof(User).Assembly.Location, "-p:AssemblyName=Hispania.Compatibility.Consumer07.Source" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        using var compiled = File.OpenRead(Path.Combine(directory.Path, "bin", "Debug", "net10.0", "Hispania.Compatibility.Consumer07.Source.dll"));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(compiled);
        compiled.Close();
        await ExerciseConsumerAsync(assembly);
    }

    private static async Task ExerciseConsumerAsync(Assembly assembly)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "test.sqlite");
        var identity = new IdentityService(new SqliteIdentityStore(path));
        var store = new SqlitePlatformAuthorizationStore(path);
        var authorization = new AuthorizationService(store, identity);
        var administration = new AuthorizationAdministrationService(store, identity, configuredBootstrapToken: "TEST-ONLY-baseline-07-bootstrap-token");
        var method = assembly.GetType("Hispania.Compatibility.Consumer07.Consumer", true)!.GetMethod("ExerciseAsync", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(typeof(IIdentityQueryV1), method.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(IAuthorizationServiceV1), method.GetParameters()[2].ParameterType);
        await (Task)method.Invoke(null, [identity, identity, authorization, administration])!;
        var user = (await identity.GetUserByUsernameAsync("baseline07"))!;
        Assert.Equal(IdentityFailure.InvalidInput, (await identity.SetUserStatusAsync(user.Id, (UserStatus)999)).Failure);
    }

    [Fact]
    public void HistoricalAssetReferenceShapeAndOpaqueValueSemantics()
    {
        var reference = BaselineCorpus.Read<AssetReference>("serialization/0.7/asset-reference.json");
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000007"), reference.Id.Value);
        Assert.Equal(new AssetReference(new AssetId(reference.Id.Value)), reference);
        Assert.Equal(reference.GetHashCode(), new AssetReference(reference.Id).GetHashCode());
        Assert.NotEqual(reference, new AssetReference(new AssetId(Guid.Empty)));
        var historical = JsonDocument.Parse(File.ReadAllText(BaselineCorpus.PathFor("serialization/0.7/asset-reference.json")));
        Assert.True(JsonElement.DeepEquals(historical.RootElement, JsonSerializer.SerializeToElement(reference)));
        Assert.Equal(reference, JsonSerializer.Deserialize<AssetReference>("{\"extra\":true,\"Id\":{\"extra\":7,\"Value\":\"00000000-0000-0000-0000-000000000007\"}}"));
        // Missing Id defaults to empty in 0.7; this is parser behavior, not a valid asset guarantee.
        Assert.Equal(Guid.Empty, JsonSerializer.Deserialize<AssetReference>("{}")!.Id.Value);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AssetReference>("{\"Id\":\"filesystem/path\"}"));
    }

    [Fact]
    public void HistoricalIdentityAndRoleJsonAndEnumPolicy()
    {
        var user = BaselineCorpus.Read<User>("serialization/0.7/identity-user.json");
        var session = BaselineCorpus.Read<Session>("serialization/0.7/identity-session.json");
        var role = BaselineCorpus.Read<Role>("serialization/0.7/authorization-role.json");
        Assert.Equal(UserStatus.Enabled, user.Status);
        Assert.Equal(user.Id, session.UserId); Assert.Null(session.RevokedAt);
        Assert.Equal("Baseline Readers", role.Name);
        foreach (var (path, value) in new (string, object)[] { ("identity-user", user), ("identity-session", session), ("authorization-role", role) })
        {
            using var old = JsonDocument.Parse(File.ReadAllText(BaselineCorpus.PathFor("serialization/0.7/" + path + ".json")));
            Assert.True(JsonElement.DeepEquals(old.RootElement, JsonSerializer.SerializeToElement(value, value.GetType())));
        }
        Assert.Equal(BaselineCorpus.Read<string[]>("serialization/0.7/permission-identifiers.json"), PlatformPermissionIds.BuiltIn.Order(StringComparer.Ordinal));
        var text = File.ReadAllText(BaselineCorpus.PathFor("serialization/0.7/identity-user.json"));
        Assert.Equal((UserStatus)999, JsonSerializer.Deserialize<User>(text.Replace("\"Status\": 0", "\"Status\": 999", StringComparison.Ordinal))!.Status);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<User>(text.Replace("\"Status\": 0", "\"Status\": \"Future\"", StringComparison.Ordinal)));
        Assert.Equal(TimeSpan.FromHours(12), JsonSerializer.Deserialize<IdentityOptions>("{}")!.EffectiveSessionLifetime);
        // CLR integer enums are open at deserialization, so services must validate and clients fail closed.
        Assert.False(Enum.IsDefined((IdentityFailure)999));
        Assert.False(new IdentityOperationResult<User>(null, (IdentityFailure)999).Succeeded);
        Assert.False(new AuthorizationOperationResult<Role>(null, (AuthorizationFailure)999).Succeeded);
    }

    [Fact]
    public async Task HistoricalModuleFramesDecodeAndNegotiate()
    {
        async Task<T?> Read<T>(string name) { using var stream = File.OpenRead(BaselineCorpus.PathFor("protocols/0.7/module-" + name + ".bin")); return await ModuleWorkerProtocolV1.ReadFrameAsync<T>(stream); }
        var hello = (await Read<ModuleWorkerHelloV1>("hello"))!;
        var negotiation = ModuleWorkerProtocolV1.Negotiate(hello, hello.ModuleId, hello.ModuleVersion, hello.AuthenticationToken);
        Assert.Equal(await Read<ModuleWorkerNegotiationV1>("negotiation"), negotiation);
        Assert.Equal("hello-07", (await Read<ModuleWorkerResponseV1>("success"))!.Payload!.Value.GetString());
        Assert.Equal("TEST_FAILURE", (await Read<ModuleWorkerResponseV1>("error"))!.Error!.Code);
        Assert.Equal("test-request-07", (await Read<ModuleWorkerCancelV1>("cancel"))!.RequestId);
        Assert.False(ModuleWorkerProtocolV1.Negotiate(hello with { MinimumProtocolVersion = 99, MaximumProtocolVersion = 99 }, hello.ModuleId, hello.ModuleVersion, hello.AuthenticationToken).Accepted);
    }

    [Fact]
    public async Task NegativeModuleFramesRejectTruncationMalformedJsonAndTolerateUnknownFields()
    {
        var bytes = File.ReadAllBytes(BaselineCorpus.PathFor("protocols/0.7/module-hello.bin"));
        using var truncated = new MemoryStream(bytes[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(truncated).AsTask());
        using var malformed = new MemoryStream(new byte[] { 1, 0, 0, 0, (byte)'{' });
        await Assert.ThrowsAsync<InvalidDataException>(() => ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(malformed).AsTask());
        foreach (var length in new[] { 0, -1, ModuleWorkerProtocolV1.DefaultMaximumFrameBytes + 1 })
        {
            var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, length);
            using var invalidLength = new MemoryStream(header);
            await Assert.ThrowsAsync<InvalidDataException>(() => ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(invalidLength).AsTask());
        }
        var hello = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(4));
        var fields = hello.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        fields["UnknownFuture"] = JsonSerializer.SerializeToElement(7);
        using var additional = new MemoryStream(); await ModuleWorkerProtocolV1.WriteFrameAsync(additional, fields); additional.Position = 0;
        Assert.Equal("hispania.extension-demo.greeting", (await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(additional))!.ModuleId);
    }

    [Fact]
    public void HistoricalWorkerFramesAndStaleGenerationCannotReplaceNewerResult()
    {
        var connect = BaselineCorpus.Read<DistributedWorkerConnectRequestV1>("protocols/0.7/worker-connect.json");
        var negotiation = BaselineCorpus.Read<DistributedWorkerConnectResponseV1>("protocols/0.7/worker-negotiation.json");
        var assignment = BaselineCorpus.Read<DistributedLeasedJobV1>("protocols/0.7/worker-assignment.json");
        var result = BaselineCorpus.Read<DistributedWorkerCompletionV1>("protocols/0.7/worker-result.json");
        var stale = BaselineCorpus.Read<DistributedWorkerCompletionV1>("protocols/0.7/worker-stale-result.json");
        Assert.Equal(1, connect.MinimumProtocolVersion); Assert.Equal(1, negotiation.SelectedProtocolVersion);
        Assert.Equal(connect.Inventory.Capabilities, negotiation.AuthorizedCapabilities);
        Assert.Equal(result.Generation, assignment.Lease.Generation); Assert.True(stale.Generation < result.Generation);
        var coordinator = new DistributedWorkCoordinator();
        const string certificate = "0707070707070707070707070707070707070707070707070707070707070707"; // Synthetic TEST identity, no certificate material.
        coordinator.AuthorizeWorker(assignment.Lease.NodeId, negotiation.AuthorizedCapabilities, certificate);
        Assert.True(coordinator.RegisterWorker(assignment.Lease.NodeId, certificate, connect.Inventory));
        var now = DateTimeOffset.UtcNow;
        var first = coordinator.Assign(assignment.Request, now, TimeSpan.FromSeconds(1), true)!;
        Assert.True(coordinator.Start(first.NodeId, first.JobId, first.Generation));
        coordinator.ExpireLeases(now.AddSeconds(2));
        coordinator.Heartbeat(first.NodeId, [], now.AddSeconds(2), TimeSpan.FromSeconds(10));
        var second = coordinator.Assign(assignment.Request, now.AddSeconds(2), TimeSpan.FromSeconds(10), true)!;
        Assert.True(second.Generation > first.Generation);
        Assert.False(coordinator.Complete(first.NodeId, first.JobId, first.Generation, stale.Result));
        Assert.True(coordinator.Start(second.NodeId, second.JobId, second.Generation));
        Assert.True(coordinator.Complete(second.NodeId, second.JobId, second.Generation, result.Result));
        Assert.False(coordinator.Complete(first.NodeId, first.JobId, first.Generation, stale.Result));
        Assert.False(coordinator.Fail(first.NodeId, first.JobId, first.Generation, "TEST_STALE"));
        Assert.Equal("valid", Assert.Single(coordinator.ListJobs()).Result!.Value.GetString());
    }

    [Fact]
    public void WorkerJsonParserRejectsMalformedAndIncompatibleShapes()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DistributedWorkerCompletionV1>("{"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DistributedWorkerCompletionV1>("{\"Generation\":\"future\"}"));
        var extended = JsonSerializer.Deserialize<DistributedWorkerCompletionV1>("{\"Future\":true,\"Result\":\"test\",\"Generation\":2}");
        Assert.Equal(2, extended!.Generation);
        // Missing optional trace metadata is preserved in the historical assignment.
        Assert.Null(BaselineCorpus.Read<DistributedLeasedJobV1>("protocols/0.7/worker-assignment.json").Request.TraceParent);
    }

    [Fact]
    public async Task HistoricalSignedPackageInstallsValidatesLoadsExecutesAndDisables()
    {
        using var directory = new TemporaryDirectory();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { typeof(Phase15CompatibilityTests).Assembly.Location, "--historical-package", directory.Path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    internal static async Task ExerciseHistoricalPackageAsync(string directory)
    {
        var manifest = BaselineCorpus.Read<ModuleManifest>("manifests/0.7/greeting.json");
        var publicKey = File.ReadAllText(BaselineCorpus.PathFor("manifests/0.7/TEST-ONLY-publisher-public.pem"));
        Assert.Equal(PublisherSignatureStatus.Trusted, ModulePackageSignature.Verify(manifest,
            new Dictionary<string, string> { ["TEST-ONLY-baseline-07"] = publicKey }, new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(PublisherSignatureStatus.Invalid, ModulePackageSignature.Verify(manifest with { Name = "tampered" },
            new Dictionary<string, string> { ["TEST-ONLY-baseline-07"] = publicKey }, new HashSet<string>(StringComparer.Ordinal)));
        var registry = new ModuleRegistry(); var runtime = new ModuleRuntime(registry);
        using var catalog = new ModuleExtensionCatalog(directory, new Version(1, 0, 0), runtime, registry,
            new Dictionary<string, Func<IModule>>(), [manifest.Id],
            trustedPublisherKeys: new Dictionary<string, string> { ["TEST-ONLY-baseline-07"] = publicKey }, requirePublisherSignature: true);
        using var package = File.OpenRead(BaselineCorpus.PathFor("packages/0.7/greeting.hispania"));
        await catalog.InstallAsync(package);
        Assert.Empty(registry.List()); Assert.True(Assert.Single(catalog.Discover()).Compatible);
        await catalog.ActivateAsync(manifest.Id, CancellationToken.None);
        ExecuteGreeting(catalog, manifest.Id);
        Assert.Equal(ModuleLifecycleState.Stopped, (await catalog.DeactivateAsync(manifest.Id, CancellationToken.None)).State);
        Assert.True(await catalog.UnloadAsync(manifest.Id));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ExecuteGreeting(ModuleExtensionCatalog catalog, string id)
    {
        using var lease = catalog.Acquire<IExtensionDemoV1>(id);
        Assert.Equal("Hello from an independently implemented extension.", lease.Value.GetMessage());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-compat-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, true); }
    }
}
