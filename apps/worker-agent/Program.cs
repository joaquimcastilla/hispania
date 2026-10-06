using Hispania.Hosting;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

try { await RunHostAsync(args); }
catch (Exception exception) when (ProductionInstallation.IsProduction(args))
{
    Environment.ExitCode = ProductionInstallation.ReportFailure(exception);
}

static async Task RunHostAsync(string[] args)
{
var production = ProductionInstallation.Load(args, "worker");
using var runtimeLease = production?.AcquireRuntimeLease();
if (production is not null)
{
    production.ValidatePrerequisites();
    if (production.CheckOnly) { ProductionInstallation.WriteCheckResult(); return; }
    production.RecordStartupIntent();
}
var builder = production is null ? Host.CreateApplicationBuilder(args) : Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production
});
if (production is not null)
{
    production.Apply(builder.Configuration);
    ProductionInstallation.AddServiceLifetime(builder.Services, "worker");
    production.ConfigureLogging(builder.Logging);
}
var coordinatorUri = builder.Configuration["DistributedWorker:CoordinatorUri"]
    ?? throw new InvalidOperationException("DistributedWorker:CoordinatorUri must be configured with an https:// URI.");
var nodeId = builder.Configuration["DistributedWorker:NodeId"]
    ?? throw new InvalidOperationException("DistributedWorker:NodeId is required.");
var certificatePath = builder.Configuration["DistributedWorker:ClientCertificatePath"]
    ?? throw new InvalidOperationException("DistributedWorker:ClientCertificatePath is required.");
var certificatePassword = builder.Configuration["DistributedWorker:ClientCertificatePassword"];
var serverPins = builder.Configuration.GetSection("DistributedWorker:CoordinatorCertificateSha256Pins").Get<string[]>() ?? [];
var workerExtensionIds = builder.Configuration.GetSection("Modules:WorkerExtensionIds").Get<string[]>() ?? [];
var trustedPublishers = builder.Configuration.GetSection("Modules:TrustedPublisherKeys").Get<Dictionary<string, string>>() ?? new(StringComparer.Ordinal);
var contractAssemblies = builder.Configuration.GetSection("DistributedWorker:ContractAssemblies").Get<string[]>() ?? [];

// Windows Schannel uses the service identity's native key container; it is outside
// managed backup state. Linux production can keep the private key ephemeral.
using var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword, production is null || OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
var registry = new ModuleRegistry();
var runtime = new ModuleRuntime(registry);
var moduleHostPath = builder.Configuration["Modules:HostPath"] ?? Path.Combine(AppContext.BaseDirectory, "module-host", "Hispania.ModuleHost.dll");
using var catalog = new ModuleExtensionCatalog(
    builder.Configuration["Modules:ExtensionsPath"] ?? Path.Combine(AppContext.BaseDirectory, "extensions"),
    new Version(1, 0, 0), runtime, registry, new Dictionary<string, Func<IModule>>(), workerExtensionIds,
    moduleHostPath, trustedPublishers, builder.Configuration.GetSection("Modules:RevokedPublisherIds").Get<string[]>(),
    requirePublisherSignature: builder.Configuration.GetValue("Modules:RequirePublisherSignature", true),
    resourceLimits: builder.Configuration.GetSection("Modules:OutOfProcessResources").Get<OutOfProcessResourceLimits>(),
    restartPolicy: builder.Configuration.GetSection("Modules:OutOfProcessRestarts").Get<OutOfProcessRestartPolicy>());
_ = catalog.Discover();
foreach (var extensionId in workerExtensionIds)
    await catalog.ActivateAsync(extensionId, CancellationToken.None).ConfigureAwait(false);

foreach (var assemblyName in contractAssemblies) _ = Assembly.Load(new AssemblyName(assemblyName));
var extensionExecutor = new ModuleExtensionCapabilityExecutor(catalog, ResolveContractType);
var capabilityHandlers = new List<IDistributedCapabilityHandlerV1>();
var maximumConcurrentJobs = Math.Clamp(builder.Configuration.GetValue("DistributedWorker:MaximumConcurrentJobs", 2), 1, 64);
var capabilityProviderAssemblies = builder.Configuration.GetSection("DistributedWorker:CapabilityProviderAssemblies").Get<string[]>() ?? [];
var providerContext = new DistributedWorkerCapabilityContextV1(new Uri(coordinatorUri), nodeId, clientCertificate, serverPins,
    builder.Configuration.AsEnumerable().Where(static pair => pair.Value is not null && !pair.Key.Contains("Password", StringComparison.OrdinalIgnoreCase) && !pair.Key.Contains("Token", StringComparison.OrdinalIgnoreCase)).ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.OrdinalIgnoreCase));
foreach (var assemblyPath in capabilityProviderAssemblies)
{
    var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
    foreach (var providerType in assembly.GetTypes().Where(static type => !type.IsAbstract && typeof(IDistributedWorkerCapabilityProviderV1).IsAssignableFrom(type)))
    {
        var provider = (IDistributedWorkerCapabilityProviderV1)(Activator.CreateInstance(providerType) ?? throw new InvalidOperationException($"Could not create capability provider {providerType.FullName}."));
        capabilityHandlers.AddRange(provider.CreateHandlers(providerContext));
    }
}
var executor = new CompositeDistributedCapabilityExecutor(extensionExecutor, capabilityHandlers);
var inventory = new DistributedWorkerInventoryV1(
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : RuntimeInformation.OSDescription,
    RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, maximumConcurrentJobs,
    builder.Configuration.GetSection("DistributedWorker:Accelerators").Get<string[]>() ?? [], executor.ListCapabilities().ToArray());
var pins = serverPins.Length > 0 ? serverPins : throw new InvalidOperationException("Configure at least one coordinator certificate SHA-256 pin.");
var options = new DistributedWorkerAgentOptions(new Uri(coordinatorUri), nodeId, clientCertificate, pins, inventory,
    TimeSpan.FromSeconds(builder.Configuration.GetValue("DistributedWorker:LeaseSeconds", 30)),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue("DistributedWorker:PollMilliseconds", 500)),
    TimeSpan.FromSeconds(builder.Configuration.GetValue("DistributedWorker:HeartbeatSeconds", 5)));

await using var agent = new DistributedWorkerAgent(options, executor);
builder.Services.AddSingleton(agent);
builder.Services.AddHostedService<WorkerAgentHostedService>();
using var host = builder.Build();
try { await host.RunAsync().ConfigureAwait(false); }
finally { foreach (var handler in capabilityHandlers.OfType<IDisposable>()) handler.Dispose(); }
return;

Type? ResolveContractType(string fullName)
{
    var extensionContract = typeof(IExtensionDemoV1);
    if (extensionContract.FullName == fullName) return extensionContract;
    return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
        .FirstOrDefault(static type => type is not null);
}

}

internal sealed class WorkerAgentHostedService(DistributedWorkerAgent agent) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => agent.RunAsync(stoppingToken);
}
