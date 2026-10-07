using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using System.Text.Json;

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
