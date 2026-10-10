using System.Text.Json;
using Hispania.ResilienceHarness;

var values = ParseArgs(args);
if (!CampaignProfiles.All.TryGetValue(values.Profile, out var profile)) throw new ArgumentException("Profile must be Smoke, Short, Soak or Extended.");
if (profile.Name == "Extended" && !values.ExplicitExtended) throw new InvalidOperationException("Extended campaigns require -ConfirmExtended.");
var campaign = values.Campaign ?? $"phase24-{profile.Name.ToLowerInvariant()}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
var manifest = values.ManifestPath is null
    ? ResilienceValidation.Create(campaign, profile, values.Seed, new(values.MaxMemoryMb ?? profile.MaxMemoryMb, values.MaxCpuPercent ?? profile.MaxCpuPercent, values.MaxConcurrency ?? profile.MaxConcurrency), values.WorkloadIntervalMilliseconds)
    : ResilienceValidation.Validate(JsonSerializer.Deserialize<ResilienceManifest>(await File.ReadAllTextAsync(Path.GetFullPath(values.ManifestPath)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Manifest JSON is empty."));
if (manifest.DurationSeconds >= CampaignProfiles.All["Extended"].DurationSeconds && !values.ExplicitExtended) throw new InvalidOperationException("Campaign manifests at Extended duration require -ConfirmExtended.");
campaign = manifest.Campaign;
var root = Path.GetFullPath(values.OutputDirectory ?? ".artifacts/phase24");
var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var artifactsRoot = Path.GetFullPath(Path.Combine(repositoryRoot, ".artifacts")) + Path.DirectorySeparatorChar;
var temporaryRoot = Path.GetFullPath(Path.GetTempPath()) + Path.DirectorySeparatorChar;
if (!root.StartsWith(artifactsRoot, StringComparison.OrdinalIgnoreCase) && !root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("OutputDirectory must be inside this repository's .artifacts directory or the operating-system temporary directory.");
Directory.CreateDirectory(root);
var owner = new CampaignOwnership(root, campaign);
var manifestPath = Path.Combine(owner.DirectoryPath, "campaign-manifest.json");
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(manifest.DurationSeconds + 45));
using var signal = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; signal.Cancel(); };
using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, signal.Token);
var runner = new CampaignRunner(manifest, owner, repositoryRoot);
var code = await runner.RunAsync(linked.Token);
if (!File.Exists(Path.Combine(owner.DirectoryPath, "campaign-summary.json"))) throw new InvalidOperationException("Harness did not emit the required campaign summary.");
Console.WriteLine($"Phase24 {manifest.Campaign}: {((JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(owner.DirectoryPath, "campaign-summary.json"))).RootElement.GetProperty("status").GetString()) ?? "FAIL")} ({owner.DirectoryPath})");
return code;

static string FindRepositoryRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Hispania.sln")) && Directory.Exists(Path.Combine(dir.FullName, "apps", "server"))) return dir.FullName;
    throw new DirectoryNotFoundException("Cannot locate the HISPANIA repository root from the harness runtime.");
}

static (string Profile, int Seed, string? OutputDirectory, string? Campaign, string? ManifestPath, int? MaxMemoryMb, int? MaxCpuPercent, int? MaxConcurrency, int WorkloadIntervalMilliseconds, bool ExplicitExtended) ParseArgs(string[] args)
{
    var profile = "Smoke"; var seed = 24001; string? output = null; string? campaign = null; string? manifest = null; int? memory = null; int? cpu = null; int? concurrency = null; var workloadInterval = 1000; var confirm = false;
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {arg}.");
        switch (arg.ToLowerInvariant())
        {
            case "--profile": profile = Value(); break;
            case "--seed": seed = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--output-directory": output = Value(); break;
            case "--campaign": campaign = Value(); break;
            case "--manifest-path": manifest = Value(); break;
            case "--max-memory-mb": memory = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--max-cpu-percent": cpu = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--max-concurrency": concurrency = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--workload-interval-milliseconds": workloadInterval = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--confirm-extended": confirm = true; break;
            default: throw new ArgumentException($"Unknown harness argument: {arg}");
        }
    }
    return (profile, seed, output, campaign, manifest, memory, cpu, concurrency, workloadInterval, confirm);
}
