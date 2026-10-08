using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

/// <summary>Bounded read-only inspection of an explicitly supplied local archive.</summary>
public static class PackageArchiveInspector
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<PackageCandidate> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (source.Length > 128L * 1024 * 1024) throw new InvalidDataException("Compressed package exceeds the size limit.");
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
        source.Position = 0;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > 512) throw new InvalidDataException("Package entry count exceeds the limit.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            ValidatePath(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Duplicate or linked archive entry.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 256L * 1024 * 1024) throw new InvalidDataException("Expanded package exceeds the size limit.");
        }
        if (!entries.TryGetValue("module.json", out var metadata) || metadata.Length > 1024 * 1024)
            throw new InvalidDataException("Missing or oversized package manifest.");
        ModuleManifest manifest;
        await using (var input = metadata.Open())
        {
            using var json = JsonDocument.Parse(await ReadManifestBytesAsync(input, cancellationToken).ConfigureAwait(false));
            RejectDuplicates(json.RootElement);
            manifest = json.RootElement.Deserialize<ModuleManifest>(Options) ?? throw new InvalidDataException("Empty package manifest.");
        }
        PackageDependencyResolver.ValidateManifest(manifest);
        if (manifest.EntryAssembly is null || manifest.EntryType is null || manifest.Files is null || !manifest.Files.ContainsKey(manifest.EntryAssembly) ||
            !entries.Keys.Where(key => key != "module.json").ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Files.Keys))
            throw new InvalidDataException("Package payload inventory does not match the manifest.");
        long actualBytes = 0;
        foreach (var pair in manifest.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ValidatePath(pair.Key);
            if (pair.Value is null || pair.Value.Length != 64 || !pair.Value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid payload digest.");
            await using var input = entries[pair.Key].Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            using var assembly = pair.Key == manifest.EntryAssembly ? new MemoryStream() : null;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                actualBytes += read;
                if (actualBytes > 256L * 1024 * 1024) throw new InvalidDataException("Actual expanded package exceeds the size limit.");
                hash.AppendData(buffer, 0, read);
                if (assembly is not null) await assembly.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(pair.Value))) throw new InvalidDataException("Payload integrity mismatch.");
            if (assembly is not null)
            {
                assembly.Position = 0;
                using var pe = new PEReader(assembly);
                if (!pe.HasMetadata) throw new InvalidDataException("Entry assembly is not a managed DLL.");
            }
        }
        if (manifest.Requires.Contains(WebContributionSchema.Requirement, StringComparer.Ordinal))
        {
            if (!entries.TryGetValue(WebContributionSchema.Path, out var web) || web.Length > WebContributionValidator.MaximumBytes)
                throw new InvalidDataException("Missing or oversized required Web descriptor.");
            await using var input = web.Open();
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > WebContributionValidator.MaximumBytes) throw new InvalidDataException("Required Web descriptor exceeds size limit.");
                content.Write(buffer, 0, read);
            }
            var descriptor = WebContributionValidator.Parse(manifest, content.ToArray());
            long assetBytes = 0;
            foreach (var asset in descriptor.Contributions.Select(page => page.Navigation.Asset).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!entries.TryGetValue(asset, out var assetEntry) || assetEntry.Length > 1024 * 1024) throw new InvalidDataException("Missing or oversized Web asset.");
                await using var assetInput = assetEntry.Open();
                using var assetContent = new MemoryStream();
                while ((read = await assetInput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    assetBytes += read;
                    if (assetContent.Length + read > 1024 * 1024 || assetBytes > 4 * 1024 * 1024) throw new InvalidDataException("Web assets exceed size limits.");
                    assetContent.Write(buffer, 0, read);
                }
                WebContributionValidator.ValidatePng(assetContent.ToArray());
            }
        }
        return new(manifest, digest, Path.GetFullPath(path));
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new InvalidDataException("Unsafe package-relative path.");
    }

    internal static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate manifest property."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    internal static async Task<byte[]> ReadManifestBytesAsync(Stream input, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > 1024 * 1024) throw new InvalidDataException("Actual package manifest exceeds the size limit.");
            content.Write(buffer, 0, read);
        }
        return content.ToArray();
    }
}
