using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;

if (args.Length != 3) return 2;
var registry = new ModuleRegistry(); var runtime = new ModuleRuntime(registry);
var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[2]))!;
using var catalog = new ModuleExtensionCatalog(args[1], new Version(1, 0, 0), runtime, registry,
    new Dictionary<string, Func<IModule>>(), ["test.package.web"], trustedPublisherKeys: keys, requirePublisherSignature: true);
var plan = await catalog.PlanPackageSetAsync([args[0]], new Dictionary<string, string> { ["test.package.web"] = "[1.0.0]" });
if (!plan.Resolved) return 3;
try
{
    await catalog.ApplyPackageSetAsync(plan, new HashSet<string> { "test.package.web" });
    Console.Error.WriteLine("Historical host activated a required Web package without supporting Web."); return 4;
}
catch (ModuleDependencyException error) when (error.Message.Contains("Required capability 'hispania.web.contributions@1.0'", StringComparison.Ordinal))
{
    if (registry.List().Count != 0 || catalog.InspectPackageGraph().Any(row => row.Healthy || row.Enabled)) return 5;
    Console.WriteLine("Exact Phase19 predecessor rejected required Dynamic Web before StartAsync and rolled back its package set."); return 0;
}
