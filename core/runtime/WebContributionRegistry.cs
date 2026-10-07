using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public sealed partial class ModuleExtensionCatalog
{
    private readonly Dictionary<string, WebContributionDocument> webDescriptors = new(StringComparer.Ordinal);
    private static readonly Meter WebMeter = new("Hispania.Runtime.Web", "1.0");
    private static readonly Histogram<int> WebActive = WebMeter.CreateHistogram<int>("hispania.web.contributions.active");
    private static readonly Counter<long> WebInvalid = WebMeter.CreateCounter<long>("hispania.web.registry.failures");

    private bool SupportsWeb => registry.List().Any(module => module is WebContributionHostModule);

    private static bool RequiresWeb(ModuleManifest manifest) => manifest.Requires.Contains(WebContributionSchema.Requirement, StringComparer.Ordinal);

    private static WebContributionDocument? ReadWebDescriptor(ModuleManifest manifest, string packageDirectory, WebContributionDocument? cached = null)
    {
        if (!manifest.Files.TryGetValue(WebContributionSchema.Path, out var expectedHash))
        {
            if (RequiresWeb(manifest)) throw new InvalidDataException("Required Web descriptor is missing.");
            return null;
        }
        var path = ResolvePackageFile(packageDirectory, WebContributionSchema.Path);
        if (new FileInfo(path).Length > WebContributionValidator.MaximumBytes) throw new InvalidDataException("Web descriptor exceeds size limit.");
        var bytes = ReadBoundedWebFile(path, WebContributionValidator.MaximumBytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expectedHash)))
            throw new InvalidDataException("Web descriptor integrity mismatch.");
        var descriptor = cached ?? WebContributionValidator.Parse(manifest, bytes);
        ValidateWebAssets(manifest, packageDirectory, descriptor);
        return descriptor;
    }

    private static void ValidateWebAssets(ModuleManifest manifest, string packageDirectory, WebContributionDocument descriptor)
    {
        long total = 0;
        foreach (var asset in descriptor.Contributions.Select(page => page.Navigation.Asset).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var path = ResolvePackageFile(packageDirectory, asset);
            var length = new FileInfo(path).Length;
            total += length;
            if (length > 1024 * 1024 || total > 4 * 1024 * 1024) throw new InvalidDataException("Web asset size exceeds bounds.");
            var bytes = ReadBoundedWebFile(path, 1024 * 1024);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(manifest.Files[asset]))) throw new InvalidDataException("Asset integrity mismatch.");
            WebContributionValidator.ValidatePng(bytes);
        }
    }

    private static byte[] ReadBoundedWebFile(string path, int maximum)
    {
        using var input = File.OpenRead(path);
        if (input.Length > maximum) throw new InvalidDataException("Web file exceeds size limit.");
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Web file exceeds size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void ValidateRequiredWeb(ModuleManifest manifest, string packageDirectory)
    {
        if (RequiresWeb(manifest)) _ = ReadWebDescriptor(manifest, packageDirectory);
    }

    /// <summary>Coherent immutable projection under the package mutation gate, with fresh policy decisions.</summary>
    public async Task<WebContributionSnapshot> InspectWebContributionsAsync(CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadPackageState();
            var graph = InspectPackageGraph().ToDictionary(row => row.PackageId, StringComparer.Ordinal);
            var contributions = new List<WebContribution>();
            var diagnostics = new List<WebContributionDiagnostic>();
            var usedHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in state.Packages.OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal))
            {
                var manifest = entry.Manifest;
                if (!manifest.Files.TryGetValue(WebContributionSchema.Path, out var hash) && !RequiresWeb(manifest)) continue;
                var row = graph[manifest.Id];
                if (!SupportsWeb || row.Status != "Resolved" || !row.Installed || !row.Trusted || !row.Authorized || !row.Enabled || !row.Healthy)
                { diagnostics.Add(new(manifest.Id, "Blocked", !SupportsWeb ? "HostUnsupported" : row.Status == "Resolved" ? "Disabled" : row.Status, 0, PackageVersion: manifest.PackageVersion)); continue; }
                try
                {
                    var packageDirectory = ResolveStoredDirectory(entry.RelativeDirectory);
                    var installed = ReadStoredManifest(packageDirectory);
                    if (!SameManifest(installed, manifest)) throw new InvalidDataException("Manifest changed.");
                    // Resolve links and hash even cached descriptor bytes; never cache security decisions.
                    var cacheKey = manifest.Id + ":" + entry.ArtifactHash + ":" + WebContributionSchema.Generation + ":" + hash;
                    webDescriptors.TryGetValue(cacheKey, out var cached);
                    cached = ReadWebDescriptor(manifest, packageDirectory, cached)!;
                    usedHashes.Add(cacheKey);
                    webDescriptors[cacheKey] = cached;
                    contributions.AddRange(cached.Contributions.Select(page => new WebContribution(manifest.Id, manifest.PackageVersion!, entry.ArtifactHash, page)));
                    diagnostics.AddRange(cached.Contributions.Select(page => new WebContributionDiagnostic(manifest.Id, "Active", "Validated", 1, page.Id, page.Route, PackageVersion: manifest.PackageVersion)));
                }
                catch (Exception error) when (error is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
                { WebInvalid.Add(1); diagnostics.Add(new(manifest.Id, "Invalid", "DescriptorValidationFailed", 0, PackageVersion: manifest.PackageVersion)); }
            }
            foreach (var key in webDescriptors.Keys.Where(key => !usedHashes.Contains(key)).ToArray()) webDescriptors.Remove(key);
            var ordered = contributions.OrderBy(item => item.Page.Navigation.Order).ThenBy(item => item.PackageId, StringComparer.Ordinal).ThenBy(item => item.Page.Id, StringComparer.Ordinal).ToArray();
            WebActive.Record(ordered.Length);
            return new(Array.AsReadOnly(ordered), diagnostics.AsReadOnly());
        }
        finally { activationGate.Release(); }
    }

    public async Task<byte[]?> ReadWebAssetAsync(string packageId, string artifactHash, string name, CancellationToken ct = default)
    {
        var relative = "web/assets/" + name;
        try { WebContributionValidator.ValidateAssetPath(relative); } catch (InvalidDataException) { return null; }
        await activationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = InspectPackageGraph().SingleOrDefault(row => row.PackageId == packageId);
            if (!SupportsWeb || row is null || row.Status != "Resolved" || !row.Trusted || !row.Authorized || !row.Enabled || !row.Healthy) return null;
            var entry = ReadPackageState().Packages.Single(entry => entry.Manifest.Id == packageId);
            if (entry.ArtifactHash != artifactHash) return null;
            var packageDirectory = ResolveStoredDirectory(entry.RelativeDirectory);
            if (!SameManifest(ReadStoredManifest(packageDirectory), entry.Manifest)) return null;
            var descriptor = ReadWebDescriptor(entry.Manifest, packageDirectory);
            if (descriptor?.Contributions.Any(page => page.Navigation.Asset == relative) != true) return null;
            var bytes = ReadBoundedWebFile(ResolvePackageFile(packageDirectory, relative), 1024 * 1024);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(entry.Manifest.Files[relative]))) return null;
            WebContributionValidator.ValidatePng(bytes);
            return bytes;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return null; }
        finally { activationGate.Release(); }
    }
}
