using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using System.Text.Json;

// Test-only, finite probes. Parent attaches the real resource policy before sending input.
if (args.Length == 2 && args[0] == "--phase21-resource-probe")
{
    Console.WriteLine("ready");
    if (Console.ReadLine() != "start") return 64;
    if (args[1] == "cpu")
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8)) Thread.SpinWait(10000);
        return 43; // The real CPU ceiling must terminate us first.
    }
    if (args[1] == "memory")
    {
        try
        {
            var retained = new List<byte[]>();
            for (var i = 0; i < 32; i++)
            {
                var block = new byte[4 * 1024 * 1024];
                Array.Fill(block, (byte)1); retained.Add(block);
            }
            GC.KeepAlive(retained);
            return 43; // Never allocate more than 128 MiB even if containment is broken.
        }
        catch (OutOfMemoryException) { return 42; }
    }
    if (args[1] == "process-count")
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--version");
            using var child = System.Diagnostics.Process.Start(start)!;
            await child.WaitForExitAsync();
            return 43; // Exactly one attempted child; never a fork bomb.
        }
        catch (System.ComponentModel.Win32Exception) { return 42; }
    }
    return 64;
}

// Test-only cross-process package-set mutation. The parent waits until each process has
// planned against the same generation, then releases both at once to exercise mutation.lock.
if (args.Length == 7 && args[0] == "--concurrent-package-mutation")
{
    var concurrentRoot = args[1]; var concurrentAction = args[2]; var concurrentPayload = args[3];
    var concurrentKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[4]))!;
    var concurrentReady = args[5]; var concurrentGate = args[6];
    var concurrentRegistry = new ModuleRegistry(); var concurrentRuntime = new ModuleRuntime(concurrentRegistry);
    using var concurrentCatalog = new ModuleExtensionCatalog(concurrentRoot, new Version(1, 0, 0), concurrentRuntime, concurrentRegistry, new Dictionary<string, Func<IModule>>(),
        ["test.package.base", "test.package.consumer"], trustedPublisherKeys: concurrentKeys, requirePublisherSignature: true);
    PackageOperationPlan? concurrentPlan = null;
    if (concurrentAction == "install")
    {
        var concurrentCandidate = await PackageArchiveInspector.InspectAsync(concurrentPayload);
        concurrentPlan = await concurrentCatalog.PlanPackageSetAsync([concurrentPayload], new Dictionary<string, string> { [concurrentCandidate.Id] = $"[{concurrentCandidate.Version}]" });
    }
    else if (concurrentAction == "remove")
        concurrentPlan = await concurrentCatalog.PlanPackageSetAsync([], new Dictionary<string, string>(), new HashSet<string>(StringComparer.Ordinal) { concurrentPayload });
    else if (concurrentAction == "enable")
        concurrentPlan = await concurrentCatalog.PlanPackageSetAsync([], new Dictionary<string, string>());
    else return 64;

    File.WriteAllText(concurrentReady, "planned");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (!File.Exists(concurrentGate)) await Task.Delay(10, timeout.Token);
    try
    {
        if (concurrentAction == "enable")
            await concurrentCatalog.ApplyPackageSetAsync(concurrentPlan!, new HashSet<string>(StringComparer.Ordinal) { concurrentPayload }, cancellationToken: timeout.Token);
        else await concurrentCatalog.ApplyPackageSetAsync(concurrentPlan!, cancellationToken: timeout.Token);
        await concurrentCatalog.StopManagedPackagesAsync(CancellationToken.None);
        return 0;
    }
    catch (InvalidOperationException exception) when (exception.Message == "Stale package operation plan.")
    {
        return 10; // Expected, controlled loser when another process committed first.
    }
}

if (args.Length != 5) return 2;
var root = args[0]; var archive = args[1]; var stage = Enum.Parse<PackageOperationStage>(args[2]); var marker = args[3];
var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[4]))!;
var registry = new ModuleRegistry(); var runtime = new ModuleRuntime(registry);
using var catalog = new ModuleExtensionCatalog(root, new Version(1, 0, 0), runtime, registry, new Dictionary<string, Func<IModule>>(),
    ["test.package.base", "test.package.consumer"], trustedPublisherKeys: keys, requirePublisherSignature: true);
await catalog.RecoverPackageSetAsync();
catalog.PackageOperationCheckpoint = current =>
{
    if (stage == PackageOperationStage.RollingBack && current == PackageOperationStage.Validated)
        throw new IOException("TEST ONLY health-boundary failure triggers rollback before process termination.");
    if (current != stage) return;
    File.WriteAllText(marker, current.ToString());
    // TEST ONLY: parent kills this process at a durable boundary. No cooperative cleanup runs.
    Thread.Sleep(Timeout.Infinite);
};
var plan = await catalog.PlanPackageSetAsync([archive], new Dictionary<string, string> { ["test.package.base"] = "[1.1.0]" });
await catalog.ApplyPackageSetAsync(plan);
return 3;
