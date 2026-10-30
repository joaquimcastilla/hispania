using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Web.Tests;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class Phase21SecurityHardeningTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task NormalRegistryPackageAndProtocolHaveRecordedDiagnosticBaselines()
    {
        using var fixture = new WebPackageFixture();
        var archive = fixture.Archive();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) Assert.Equal(WebPackageFixture.Id, (await PackageArchiveInspector.InspectAsync(archive)).Id);
        output.WriteLine("Ten normal package inspections: {0} ms; no SLA.", timer.ElapsedMilliseconds);
        timer.Restart();
        for (var i = 0; i < 100; i++) await fixture.Catalog.InspectWebContributionsAsync();
        output.WriteLine("One hundred normal empty registry snapshots: {0} ms; no SLA.", timer.ElapsedMilliseconds);
        var hello = new ModuleWorkerHelloV1("test.package", "1.0.0", ModuleWorkerProtocolV1.ProtocolId, 1, 1, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        timer.Restart();
        for (var i = 0; i < 1000; i++)
        {
            using var stream = new MemoryStream(); await ModuleWorkerProtocolV1.WriteFrameAsync(stream, hello); stream.Position = 0;
            Assert.Equal(hello, await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerHelloV1>(stream));
        }
        output.WriteLine("One thousand normal protocol roundtrips: {0} ms; no SLA.", timer.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData(2141)][InlineData(2142)]
    public void DependencyRangeAndCanonicalIdCorpusFailsWithControlledErrors(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 128; i++)
        {
            var version = $"{random.Next(20)}.{random.Next(20)}.{random.Next(20)}";
            var range = PackageDependencyResolver.ParseRange($"[{version}]");
            Assert.True(range.Satisfies(NuGet.Versioning.NuGetVersion.Parse(version)));
            Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ParseRange($"[{version},*]"));
            var id = "test.package-" + random.Next();
            PackageDependencyResolver.ValidateId(id);
            Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ValidateId(id + "/../escape"));
        }
    }

    [Theory]
    [InlineData("cpu")][InlineData("memory")]
    public async Task FiniteChildProbesAreContainedByRealResourcePolicy(string mode)
    {
        await RunResourceProbe(mode);
        if (mode == "memory" && OperatingSystem.IsWindows()) await RunResourceProbe("process-count");
    }

    private static async Task RunResourceProbe(string mode)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Hispania.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var probe = Path.Combine(root.FullName, "tests", "package-lifecycle-process-host", "bin", configuration, "net10.0", "Hispania.PackageLifecycle.ProcessHost.dll");
        Assert.True(File.Exists(probe));
        var policy = new OutOfProcessResourceLimits { MaximumMemoryBytes = 64L * 1024 * 1024, MaximumCpuSeconds = 1, MaximumCpuRatePercent = 100, MaximumWindowsProcessCount = 1, RequireOperatingSystemLimits = true };
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(probe); start.ArgumentList.Add("--phase21-resource-probe"); start.ArgumentList.Add(mode);
        typeof(OutOfProcessResourceLimits).GetMethod("Configure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(policy, [start]);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var process = System.Diagnostics.Process.Start(start)!;
        IDisposable? limits = null;
        var readyReceived = false;
        var startSent = false;
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            readyReceived = true;
            limits = (IDisposable?)typeof(OutOfProcessResourceLimits).GetMethod("ApplyTo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(policy, [process]);
            if (OperatingSystem.IsWindows()) Assert.NotNull(limits);
            var stdoutCapture = new System.Text.StringBuilder();
            var stderrCapture = new System.Text.StringBuilder();
            var output = BoundedProcessOutput.DrainAsync(process.StandardOutput, stdoutCapture);
            var error = BoundedProcessOutput.DrainAsync(process.StandardError, stderrCapture);
            await process.StandardInput.WriteLineAsync("start"); await process.StandardInput.FlushAsync();
            startSent = true;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.WhenAll(output, error);
            var expectedExit = mode == "cpu" ? process.ExitCode is not 43 and not 64 : process.ExitCode == 42;
            if (!expectedExit)
            {
                var diagnostics = mode == "memory" ? MemoryProbeFailureDiagnostics(stderrCapture) : "No memory-probe stderr emitted.";
                var exitCodeHex = unchecked((uint)process.ExitCode).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                Assert.Fail(
                    $"Resource probe exited unexpectedly: mode={mode}; exitCode={process.ExitCode}; exitCodeHex=0x{exitCodeHex}; " +
                    $"readyReceived={readyReceived}; startSent={startSent}; limitsAttached={limits is not null}; " +
                    $"windowsProcessMemoryBytes={policy.MaximumMemoryBytes}; windowsJobMemoryBytes={policy.MaximumMemoryBytes}; " +
                    $"heapHardLimitHex={policy.MaximumMemoryBytes:X}; " +
                    $"cpuSeconds={policy.MaximumCpuSeconds}; cpuRatePercent={policy.MaximumCpuRatePercent}; " +
                    $"windowsProcessLimit={policy.MaximumWindowsProcessCount}; requireOperatingSystemLimits={policy.RequireOperatingSystemLimits}; " +
                    $"elapsedMs={timer.ElapsedMilliseconds}; {diagnostics}");
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            limits?.Dispose();
        }
    }

    private static string MemoryProbeFailureDiagnostics(System.Text.StringBuilder stderrCapture)
    {
        string captured;
        lock (stderrCapture) captured = stderrCapture.ToString();
        var lines = captured.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("PHASE21_MEMORY_PROBE mode=memory ", StringComparison.Ordinal) ||
                           line is "Stack overflow." or "Process terminated." or "The process was terminated due to StackOverflowException.")
            .TakeLast(8)
            .Select(line => line.Length <= 180 ? line : line[..180])
            .ToArray();
        var tail = lines.Length == 0 ? "<none>" : string.Join(" | ", lines);
        var lastMilestone = lines.Length == 0 ? "<none>" : lines[^1];
        return $"lastMilestone={lastMilestone}; stderrTail={tail}";
    }

    [Fact]
    public async Task NewlineFreeChildOutputIsDrainedWithoutUnboundedCapture()
    {
        using var reader = new StringReader(new string('x', 4 * 1024 * 1024));
        var capture = new System.Text.StringBuilder();
        await BoundedProcessOutput.DrainAsync(reader, capture);
        Assert.Equal(BoundedProcessOutput.MaximumCharacters, capture.Length);
        Assert.Equal(-1, reader.Read());
    }
    [Theory]
    [InlineData("{\"Kind\":\"hello\",\"Kind\":\"stop\",\"Payload\":{}}")]
    [InlineData("{\"Kind\":\"unknown-opcode\",\"Payload\":{}}")]
    [InlineData("{\"Kind\":\"hello\",\"Payload\":null}")]
    public async Task InvalidProtocolEnvelopesCannotReachDispatch(string json)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var bytes = new byte[payload.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length); payload.CopyTo(bytes, 4);
        using var source = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(source));
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public void WorkerOversizedResultAndHeartbeatCannotChangeValidState()
    {
        var coordinator = new DistributedWorkCoordinator();
        var capability = new DistributedCapabilityV1("test.operation", "1.0");
        var pin = new string('1', 64);
        coordinator.AuthorizeWorker("worker", [capability], pin);
        coordinator.RegisterWorker("worker", pin, new("Linux", "x64", 1, 1024, 1, [], [capability]));
        var job = new DistributedJobRequestV1("job", capability.ContractId, capability.ContractVersion, "run", JsonSerializer.SerializeToElement(new { text = "normal" }));
        coordinator.Queue(job, allowRemote: true);
        var lease = coordinator.LeaseNext("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))!;
        Assert.False(coordinator.Complete("worker", "job", lease.Lease.Generation, JsonSerializer.SerializeToElement(new string('x', 1024 * 1024))));
        Assert.Throws<ArgumentException>(() => coordinator.Heartbeat("worker", Enumerable.Repeat("job", 1025).ToArray(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.True(coordinator.Complete("worker", "job", lease.Lease.Generation, JsonSerializer.SerializeToElement(new { result = "normal" })));
        Assert.False(coordinator.Complete("worker", "job", lease.Lease.Generation, JsonSerializer.SerializeToElement(new { result = "duplicate" })));
    }
    [Theory]
    [InlineData("requires")][InlineData("permissions")][InlineData("files")][InlineData("implements")]
    public void NullManifestCollectionsAreRejectedAsMalformedInput(string field)
    {
        var manifest = WebPackageFixture.Manifest();
        manifest = field switch { "requires" => manifest with { Requires = null! }, "permissions" => manifest with { Permissions = null! }, "files" => manifest with { Files = null! }, _ => manifest with { Implements = null! } };
        Assert.Throws<InvalidDataException>(() => PackageDependencyResolver.ValidateManifest(manifest));
    }

    [Theory]
    [InlineData("capabilities")][InlineData("accelerators")][InlineData("entry")][InlineData("long")]
    public void MalformedWorkerInventoryIsRejectedWithoutNullDereference(string field)
    {
        var inventory = new DistributedWorkerInventoryV1("Linux", "x64", 1, 1024, 1, [], []);
        inventory = field switch { "capabilities" => inventory with { Capabilities = null! }, "accelerators" => inventory with { Accelerators = null! }, "entry" => inventory with { Capabilities = [null!] }, _ => inventory with { Accelerators = [new string('a', 129)] } };
        Assert.Throws<ArgumentException>(() => new DistributedWorkCoordinator().RegisterWorker("worker", new string('1', 64), inventory));
    }
    [Theory]
    [InlineData(2101)][InlineData(2102)][InlineData(2103)]
    public async Task BoundedDeterministicMalformedFrameCorpus(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 128; i++)
        {
            var payload = new byte[random.Next(1, 256)]; random.NextBytes(payload);
            var bytes = new byte[payload.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length); payload.CopyTo(bytes, 4);
            using var source = new MemoryStream(bytes);
            var error = await Record.ExceptionAsync(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(source, 4096));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"seed={seed}; case={i}; unexpected exception type={error?.GetType().Name}");
        }
        foreach (var length in new[] { int.MinValue, -1, 0, 4097, int.MaxValue })
        {
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
            using var source = new MemoryStream(bytes);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(source, 4096));
        }
        var nested = System.Text.Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65));
        var deepFrame = new byte[nested.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(deepFrame, nested.Length); nested.CopyTo(deepFrame, 4);
        using var deepSource = new MemoryStream(deepFrame);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(deepSource));
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("\r\nFAKE LOG")]
    public void MalformedModuleAuthenticationTokenFailsClosed(string? token)
    {
        var hello = new ModuleWorkerHelloV1("test.package", "1.0.0", ModuleWorkerProtocolV1.ProtocolId, 1, 1, token!);
        Assert.False(ModuleWorkerProtocolV1.Negotiate(hello, "test.package", "1.0.0", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).Accepted);
    }

    [Theory]
    [InlineData(2111)][InlineData(2112)]
    public void WebSchemaDeterministicMutationsPreserveValidTextAndRejectUnsafeRoutes(int seed)
    {
        var random = new Random(seed);
        var manifest = WebPackageFixture.Manifest();
        var page = WebPackageFixture.Document().Contributions[0];
        for (var i = 0; i < 128; i++)
        {
            var text = "<script>canary</script>" + random.Next();
            var valid = new WebContributionDocument(1, [page with { Title = text }]);
            Assert.Equal(text, WebContributionValidator.Parse(manifest, JsonSerializer.SerializeToUtf8Bytes(valid, Json)).Contributions[0].Title);
            var invalid = valid with { Contributions = [page with { Route = "../" + random.Next() }] };
            Assert.Throws<InvalidDataException>(() => WebContributionValidator.Parse(manifest, JsonSerializer.SerializeToUtf8Bytes(invalid, Json)));
        }
    }

    [Theory]
    [InlineData("../module.json")][InlineData("/module.json")][InlineData("web\\module.json")]
    [InlineData("module.json:stream")][InlineData("./module.json")]
    public async Task ArchiveTraversalNeverReachesExtraction(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "phase21-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var output = File.Create(path))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("{}");
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageArchiveInspector.InspectAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ArchiveDuplicateCaseCollisionsAndRandomCentralDirectoriesAreRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "phase21-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var output = File.Create(path))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var name in new[] { "module.json", "MODULE.JSON" })
                { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("{}"); }
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageArchiveInspector.InspectAsync(path));
            var random = new Random(2121);
            for (var i = 0; i < 64; i++)
            {
                var bytes = new byte[random.Next(1, 1024)]; random.NextBytes(bytes);
                await File.WriteAllBytesAsync(path, bytes);
                await Assert.ThrowsAsync<InvalidDataException>(() => PackageArchiveInspector.InspectAsync(path));
            }
        }
        finally { File.Delete(path); }
    }
}
