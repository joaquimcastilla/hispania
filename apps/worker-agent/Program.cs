using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Transcoding.FFmpeg;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
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

using var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword, X509KeyStorageFlags.UserKeySet);
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
DistributedFfmpegCapabilityHandler? ffmpegHandler = null;
if (builder.Configuration.GetValue("DistributedWorker:Ffmpeg:Enabled", false))
{
    var ffmpegOptions = builder.Configuration.GetSection("DistributedWorker:Ffmpeg:WorkerPolicy").Get<FfmpegWorkerPolicy>() ?? new FfmpegWorkerPolicy();
    maximumConcurrentJobs = Math.Min(maximumConcurrentJobs, ffmpegOptions.MaximumConcurrentJobs);
    var workerAssembly = builder.Configuration["DistributedWorker:Ffmpeg:WorkerAssemblyPath"] ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg-worker", "Hispania.FFmpeg.Worker.dll");
    var localCache = builder.Configuration["DistributedWorker:Ffmpeg:CachePath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "remote-transcode-cache");
    var ffmpegPath = builder.Configuration["DistributedWorker:Ffmpeg:ExecutablePath"] ?? "ffmpeg";
    ffmpegHandler = new DistributedFfmpegCapabilityHandler(new Uri(coordinatorUri), nodeId, clientCertificate, serverPins, workerAssembly, ffmpegPath, localCache, ffmpegOptions);
    capabilityHandlers.Add(ffmpegHandler);
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

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
await using var agent = new DistributedWorkerAgent(options, executor);
Console.WriteLine($"HISPANIA worker '{nodeId}' starting with {inventory.Capabilities.Count} active capabilities.");
try { await agent.RunAsync(stop.Token).ConfigureAwait(false); }
finally { ffmpegHandler?.Dispose(); }
return;

Type? ResolveContractType(string fullName)
{
    var extensionContract = typeof(IExtensionDemoV1);
    if (extensionContract.FullName == fullName) return extensionContract;
    return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
        .FirstOrDefault(static type => type is not null);
}
