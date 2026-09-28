using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public sealed record InstalledExtensionPackage(string Id, Version Version, string Directory, ModuleManifest Manifest);

/// <summary>Validates and installs local .hispania ZIP packages as inert files; it never loads package code.</summary>
public sealed class ExtensionPackageInstaller
{
    private const int MaximumEntries = 512;
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string root;
    private readonly Version hostVersion;
    private readonly Func<ModuleManifest, string?> validateContracts;

    public ExtensionPackageInstaller(string root, Version hostVersion, IEnumerable<string> allowlistedIds, Func<ModuleManifest, string?>? validateContracts = null)
    {
        this.root = Path.GetFullPath(root);
        this.hostVersion = hostVersion;
        ArgumentNullException.ThrowIfNull(allowlistedIds);
        this.validateContracts = validateContracts ?? (_ => null);
    }

    public async Task<InstalledExtensionPackage> InstallAsync(Stream package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var packageCopy = new MemoryStream();
        var packageBuffer = new byte[81920];
        long packageBytes = 0;
        int packageRead;
        while ((packageRead = await package.ReadAsync(packageBuffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            packageBytes = checked(packageBytes + packageRead);
            if (packageBytes > MaximumPackageBytes) throw new InvalidDataException("Compressed package exceeds the configured size limit.");
            await packageCopy.WriteAsync(packageBuffer.AsMemory(0, packageRead), cancellationToken).ConfigureAwait(false);
        }
        packageCopy.Position = 0;
        using var archive = new ZipArchive(packageCopy, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 1 or > MaximumEntries) throw new InvalidDataException("Package entry count is outside the allowed range.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            ValidateRelativePath(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException($"Duplicate package path '{entry.FullName}'.");
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Symbolic links are not allowed in packages.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (entry.Length < 0 || expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("Expanded package size exceeds the configured limit.");
        }
        if (!entries.TryGetValue("module.json", out var manifestEntry)) throw new InvalidDataException("Package must contain a root module.json.");
        ModuleManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ModuleManifest>(manifestStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Package manifest is empty.");
        }
        ValidateManifest(manifest);
        // Installation stores verified bytes only. Authorization is checked at activation time by
        // ModuleExtensionCatalog, and execution is performed only after that separate decision.
        var contractError = validateContracts(manifest);
        if (contractError is not null) throw new InvalidDataException(contractError);
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("Package manifest must list SHA-256 digests for payload files.");
        if (!entries.Keys.Where(path => path != "module.json").ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Files.Keys))
            throw new InvalidDataException("Package payload files do not match the manifest file list.");
        if (!manifest.Files.TryGetValue(manifest.EntryAssembly!, out _)) throw new InvalidDataException("Entry assembly is not covered by the package integrity manifest.");

        var version = Version.Parse(manifest.Version);
        var destination = Path.GetFullPath(Path.Combine(root, manifest.Id, version.ToString()));
        EnsureWithinRoot(destination);
        if (Directory.Exists(destination)) throw new IOException($"Extension version '{manifest.Id}@{version}' is already installed.");
        Directory.CreateDirectory(root);
        var moduleDirectory = Path.Combine(root, manifest.Id);
        if (Directory.Exists(moduleDirectory) && (File.GetAttributes(moduleDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Extension version directory cannot be a reparse point.");
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            long actualExpandedBytes = 0;
            foreach (var (relativePath, entry) in entries.Where(pair => pair.Key != "module.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                EnsureWithinRoot(staging, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    actualExpandedBytes = checked(actualExpandedBytes + read);
                    if (actualExpandedBytes > MaximumExpandedBytes)
                        throw new InvalidDataException("Actual expanded package size exceeds the configured limit.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(manifest.Files[relativePath])))
                    throw new InvalidDataException($"Integrity check failed for '{relativePath}'.");
            }
            var copiedManifest = Path.Combine(staging, "module.json");
            await using (var source = manifestEntry.Open())
            await using (var output = new FileStream(copiedManifest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            var entryAssembly = manifest.EntryAssembly!;
            if (Path.GetExtension(entryAssembly).Equals(".dll", StringComparison.OrdinalIgnoreCase) == false)
                throw new InvalidDataException("Entry assembly must be a DLL.");
            try { _ = AssemblyName.GetAssemblyName(Path.Combine(staging, entryAssembly.Replace('/', Path.DirectorySeparatorChar))); }
            catch (BadImageFormatException exception) { throw new InvalidDataException("Entry assembly is not a valid managed DLL.", exception); }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if ((File.GetAttributes(moduleDirectory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Extension version directory cannot be a reparse point.");
            Directory.Move(staging, destination);
            return new InstalledExtensionPackage(manifest.Id, version, destination, manifest);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private void ValidateManifest(ModuleManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new InvalidDataException("Invalid module id.");
        if (!Version.TryParse(manifest.Version, out _) || !Version.TryParse(manifest.MinimumHostVersion, out var minimum) || hostVersion < minimum)
            throw new InvalidDataException("Invalid module version or incompatible minimum host version.");
        if (manifest.MaximumHostVersion is not null && (!Version.TryParse(manifest.MaximumHostVersion, out var maximum) || hostVersion > maximum))
            throw new InvalidDataException("Host version is outside the extension compatibility range.");
        if (manifest.Runtime is not ("dotnet-inprocess-trusted" or "dotnet-outofprocess")) throw new InvalidDataException("Unsupported extension runtime.");
        if (manifest.PackageFormatVersion != 1) throw new InvalidDataException($"Unsupported extension package format version {manifest.PackageFormatVersion}.");
        if (manifest.Implements is null || manifest.Provides is null || manifest.Requires is null || manifest.Dependencies is null || manifest.OptionalDependencies is null || manifest.Permissions is null)
            throw new InvalidDataException("Manifest arrays cannot be null.");
        foreach (var contract in manifest.Implements.Concat(manifest.Provides).Concat(manifest.Requires))
        {
            if (string.IsNullOrWhiteSpace(contract)) throw new InvalidDataException("Contract declarations cannot be empty.");
            var separator = contract.LastIndexOf('@');
            if (separator <= 0 || !Version.TryParse(contract[(separator + 1)..], out _)) throw new InvalidDataException($"Invalid versioned contract declaration '{contract}'.");
        }
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType)) throw new InvalidDataException("EntryAssembly and EntryType are required.");
        if (manifest.Runtime == "dotnet-outofprocess" && (manifest.ContractTypes is null || manifest.Provides.Any(capability => !manifest.ContractTypes.ContainsKey(capability))))
            throw new InvalidDataException("Every out-of-process provided capability requires an explicit public CLR contract type.");
        if (manifest.ContractTypes is null || manifest.ContractTypes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || !manifest.Provides.Contains(pair.Key, StringComparer.Ordinal)))
            throw new InvalidDataException("Public contract type mappings must refer to declared provided capabilities.");
        ValidateRelativePath(manifest.EntryAssembly);
        if (manifest.Files is null || manifest.Files.Values.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit))) throw new InvalidDataException("Every payload file must have a SHA-256 digest.");
        foreach (var dependency in manifest.Dependencies ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency) || dependency.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new InvalidDataException("Invalid dependency module id.");
            if (!Directory.Exists(Path.Combine(root, dependency))) throw new InvalidDataException($"Required dependency '{dependency}' is not installed.");
        }
        if (manifest.DependencyMinimumVersions is null || manifest.DependencyMinimumVersions.Keys.Any(key => !manifest.Dependencies.Contains(key, StringComparer.Ordinal)) ||
            manifest.DependencyMinimumVersions.Values.Any(value => !Version.TryParse(value, out _)))
            throw new InvalidDataException("Invalid minimum module dependency versions.");
        foreach (var path in manifest.Files.Keys) ValidateRelativePath(path);
    }

    private void EnsureWithinRoot(string path) => EnsureWithinRoot(root, path);

    private static void EnsureWithinRoot(string basePath, string path)
    {
        var rootPath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escapes the authorized extension directory.");
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) || path.Split('/').Any(segment => segment is "" or "." or "..") || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException($"Unsafe package path '{path}'.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Any(character => character < 32 || character is '<' or '>' or '"' or '|' or '?' or '*') || segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException($"Unsafe package path '{path}'.");
            var deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && deviceName[3] is >= '1' and <= '9')
                throw new InvalidDataException($"Unsafe package path '{path}'.");
        }
    }
}
