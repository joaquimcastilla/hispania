using System.Security.Cryptography;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hispania.CoreArtifactValidator;

public sealed record ArtifactDescriptor(
    int SchemaVersion,
    string ArtifactId,
    string SourceCommit,
    string TargetFramework,
    string RuntimeIdentifier,
    string PublishMode,
    IReadOnlyList<ResolvedDependency> Dependencies,
    IReadOnlyDictionary<string, string>? FileOrigins = null);

public sealed record ResolvedDependency(string Kind, string Id, string Version, string Phase, bool Direct = false);
public sealed record ArtifactFile(string Path, long Size, string Sha256, string FileType, string? Origin);
public sealed record PolicyExceptionRecord(string Id, string Justification, string Scope, string Evidence, string Approval, DateTimeOffset ReviewByUtc);
public sealed record PolicyResult(string PolicyId, string Status, IReadOnlyList<string> Issues);

public sealed record ArtifactManifest(
    int SchemaVersion,
    string ArtifactId,
    string SourceCommit,
    string TargetFramework,
    string RuntimeIdentifier,
    string PublishMode,
    DateTimeOffset GeneratedAtUtc,
    string ContentId,
    IReadOnlyList<ArtifactFile> Files,
    IReadOnlyList<ResolvedDependency> Dependencies,
    IReadOnlyList<PolicyExceptionRecord> Exceptions,
    PolicyResult PolicyResult);

public sealed record AllowedDependency(string Kind, string Id, string Version, string[] Phases, string Status, string Rationale, string? AssemblyName = null);
public sealed record ArtifactPolicy(
    int SchemaVersion,
    string PolicyId,
    string[] SupportedTargetFrameworks,
    string[] SupportedRuntimeIdentifiers,
    string[] SupportedPublishModes,
    string[] RequiredFiles,
    AllowedDependency[] AllowedDependencies,
    string[] ForbiddenDependencyPrefixes,
    string[] AllowedFilePatterns,
    string[] ConditionalFilePatterns,
    string[] ExcludedSha256,
    string[] ForbiddenPathPatterns);

public sealed record ValidationReport(bool Passed, IReadOnlyList<string> Issues, string? ContentId, string? PolicyId);

public static class ManifestJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string ContentId(ArtifactManifest manifest)
    {
        var payload = new ContentIdentity(
            manifest.SchemaVersion,
            manifest.ArtifactId,
            manifest.SourceCommit,
            manifest.TargetFramework,
            manifest.RuntimeIdentifier,
            manifest.PublishMode,
            manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            manifest.Dependencies.OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Phase, StringComparer.Ordinal)
            .ThenBy(item => item.Direct).ToArray(),
            manifest.Exceptions.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, Options)));
    }

    private sealed record ContentIdentity(int SchemaVersion, string ArtifactId, string SourceCommit, string TargetFramework,
        string RuntimeIdentifier, string PublishMode, ArtifactFile[] Files, ResolvedDependency[] Dependencies, PolicyExceptionRecord[] Exceptions);
}

public static class ArtifactManifestBuilder
{
    public const string ManifestFileName = "core-artifact-manifest.json";

    public static ArtifactManifest Create(string root, ArtifactDescriptor descriptor, ArtifactPolicy policy, DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(policy);
        if (descriptor.SchemaVersion != 1) throw new InvalidDataException($"Unsupported descriptor schema version: {descriptor.SchemaVersion}.");
        if (descriptor.Dependencies is null) throw new InvalidDataException("Descriptor dependencies are required.");
        if (policy.AllowedDependencies is null) throw new InvalidDataException("Policy allowlist is missing.");
        var normalizedRoot = Path.GetFullPath(root);
        if (!Directory.Exists(normalizedRoot)) throw new DirectoryNotFoundException("Artifact root does not exist.");
        RejectLink(new DirectoryInfo(normalizedRoot));

        var files = new List<ArtifactFile>();
        Walk(normalizedRoot, normalizedRoot, files, descriptor.FileOrigins ?? new Dictionary<string, string>(StringComparer.Ordinal));
        var published = ReadPublishDependencies(normalizedRoot, files, descriptor.ArtifactId, descriptor.TargetFramework, descriptor.RuntimeIdentifier, policy);
        var allDependencies = descriptor.Dependencies.Concat(published)
            .OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal).ThenBy(item => item.Phase, StringComparer.Ordinal).ThenBy(item => item.Direct).ToArray();
        var manifest = new ArtifactManifest(1, descriptor.ArtifactId, descriptor.SourceCommit, descriptor.TargetFramework,
            descriptor.RuntimeIdentifier, descriptor.PublishMode, (generatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            string.Empty, files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            allDependencies,
            [],
            new PolicyResult(policy.PolicyId, "not-evaluated", []));
        manifest = manifest with { ContentId = ManifestJson.ContentId(manifest) };
        return manifest;
    }

    internal static IReadOnlyList<ResolvedDependency> ReadPublishDependencies(string root, IReadOnlyList<ArtifactFile> files,
        string artifactId, string targetFramework, string rid, ArtifactPolicy policy)
    {
        var depsFiles = files.Where(file => file.FileType == "dependencyManifest").ToArray();
        var rootDeps = depsFiles.Where(file => Path.GetDirectoryName(file.Path) is null or "").ToArray();
        if (rootDeps.Length != 1) throw new InvalidDataException("Exactly one root Core .deps.json file is required.");
        if (depsFiles.Any(file => file != rootDeps[0] && file.Path != "module-host/Hispania.ModuleHost.deps.json"))
            throw new InvalidDataException("Only the approved module-host process may add a nested dependency manifest.");
        var dependencies = new List<ResolvedDependency>();
        foreach (var depsFile in depsFiles)
        {
            var hostDirectory = Path.GetDirectoryName(depsFile.Path)?.Replace('\\', '/') ?? "";
            var assemblyName = Path.GetFileName(depsFile.Path)[..^".deps.json".Length];
            var processId = hostDirectory.Length == 0 ? artifactId : assemblyName;
            if (hostDirectory.Length != 0 && (hostDirectory != "module-host" || assemblyName != "Hispania.ModuleHost"))
                throw new InvalidDataException("Nested dependency manifests are restricted to the approved ModuleHost.");
            var runtimeConfigPath = (hostDirectory.Length == 0 ? "" : hostDirectory + "/") + assemblyName + ".runtimeconfig.json";
            var runtimeConfig = files.SingleOrDefault(file => file.Path == runtimeConfigPath && file.FileType == "runtimeConfig")
                ?? throw new InvalidDataException($"Runtime configuration is missing for {assemblyName}.");
            var expectedDll = (hostDirectory.Length == 0 ? "" : hostDirectory + "/") + assemblyName + ".dll";
            if (!files.Any(file => file.Path == expectedDll)) throw new InvalidDataException($"Process assembly is missing for {assemblyName}.");

            using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, depsFile.Path.Replace('/', Path.DirectorySeparatorChar)))))
            {
            var json = document.RootElement;
            var targetName = json.GetProperty("runtimeTarget").GetProperty("name").GetString()
                ?? throw new InvalidDataException(".deps.json runtimeTarget.name is missing.");
            var frameworkVersion = targetFramework.StartsWith("net", StringComparison.Ordinal) ? targetFramework[3..] : targetFramework;
            if (!targetName.StartsWith(".NETCoreApp,Version=v" + frameworkVersion + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($".deps.json target does not match target framework {targetFramework}.");
            if (!targetName.EndsWith("/" + rid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($".deps.json runtime target does not identify selected RID {rid}.");
            var target = json.GetProperty("targets").GetProperty(targetName);
            var libraries = json.GetProperty("libraries");
            var directIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var application in target.EnumerateObject().Where(item => item.Name.Split('/')[0].Equals(processId, StringComparison.OrdinalIgnoreCase)))
                if (application.Value.TryGetProperty("dependencies", out var direct))
                    foreach (var dependency in direct.EnumerateObject()) directIds.Add(dependency.Name);

            foreach (var library in target.EnumerateObject())
            {
                var separator = library.Name.LastIndexOf('/');
                if (separator <= 0 || separator == library.Name.Length - 1) throw new InvalidDataException($"Malformed .deps.json library key: {library.Name}.");
                var name = library.Name[..separator];
                var version = library.Name[(separator + 1)..];
                if (name.Equals(processId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!libraries.TryGetProperty(library.Name, out var metadata) || !metadata.TryGetProperty("type", out var typeElement))
                    throw new InvalidDataException($".deps.json library metadata is missing for {library.Name}.");
                var type = typeElement.GetString();
                if (string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                {
                    dependencies.Add(new("package", name, version, "runtime", directIds.Contains(name)));
                    dependencies.Add(new("package", name, version, "publish", directIds.Contains(name)));
                }
                else if (string.Equals(type, "project", StringComparison.OrdinalIgnoreCase))
                {
                    var knownProject = policy.AllowedDependencies.FirstOrDefault(candidate => candidate.Kind == "project" &&
                        string.Equals(candidate.AssemblyName, name, StringComparison.OrdinalIgnoreCase));
                    var projectId = knownProject?.Id ?? name;
                    dependencies.Add(new("project", projectId, "source", "runtime", directIds.Contains(name)));
                    dependencies.Add(new("project", projectId, "source", "publish", directIds.Contains(name)));
                }
                else if (!string.Equals(type, "framework", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unknown .deps.json library type '{type}' for {name}.");

                if (library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets))
                {
                    foreach (var asset in runtimeTargets.EnumerateObject())
                    {
                        if (!asset.Value.TryGetProperty("rid", out var ridElement) || !string.Equals(ridElement.GetString(), rid, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Runtime asset does not match selected RID {rid}: {asset.Name}.");
                        if (asset.Value.TryGetProperty("assetType", out var assetType) && string.Equals(assetType.GetString(), "native", StringComparison.OrdinalIgnoreCase))
                            dependencies.Add(new("runtimeAsset", name + ":" + asset.Name.Replace('\\', '/'), version, "publish", directIds.Contains(name)));
                    }
                }
            }
            }
            using var runtimeDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, runtimeConfig.Path.Replace('/', Path.DirectorySeparatorChar))));
            {
            var options = runtimeDocument.RootElement.GetProperty("runtimeOptions");
            if (!string.Equals(options.GetProperty("tfm").GetString(), targetFramework, StringComparison.Ordinal))
                throw new InvalidDataException(".runtimeconfig.json target framework does not match the manifest.");
            if (options.TryGetProperty("framework", out var framework))
                AddFramework(framework);
            if (options.TryGetProperty("frameworks", out var frameworks))
                foreach (var item in frameworks.EnumerateArray()) AddFramework(item);

            void AddFramework(JsonElement item)
            {
                var name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Runtime framework name is missing.");
                var version = item.GetProperty("version").GetString() ?? throw new InvalidDataException("Runtime framework version is missing.");
                dependencies.Add(new("framework", name, version, "runtime", true));
                dependencies.Add(new("framework", name, version, "publish", true));
            }
            }
        }
        var unique = dependencies.GroupBy(item => $"{item.Kind}|{item.Id}|{item.Version}|{item.Phase}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Direct = group.Any(item => item.Direct) }).ToArray();
        return unique;
    }

    private static void Walk(string root, string directory, List<ArtifactFile> files, IReadOnlyDictionary<string, string> origins)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links and reparse points are not allowed: {Relative(root, entry)}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Walk(root, entry, files, origins);
                continue;
            }

            var relative = Relative(root, entry);
            if (string.Equals(relative, ManifestFileName, StringComparison.Ordinal)) continue;
            var info = new FileInfo(entry);
            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            origins.TryGetValue(relative, out var origin);
            files.Add(new ArtifactFile(relative, info.Length, hash, Classify(relative, entry), origin));
        }
    }

    private static string Relative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    internal static string Classify(string path, string physicalPath)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".deps.json", StringComparison.Ordinal)) return "dependencyManifest";
        if (lower.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)) return "runtimeConfig";
        if (lower.EndsWith(".so", StringComparison.Ordinal) || lower.EndsWith(".dylib", StringComparison.Ordinal)) return "nativeRuntime";
        if (lower.EndsWith(".dll", StringComparison.Ordinal) || lower.EndsWith(".exe", StringComparison.Ordinal))
        {
            try
            {
                using var stream = File.OpenRead(physicalPath);
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (pe.PEHeaders.PEHeader is null)
                    throw new InvalidDataException($"Executable image is not a valid PE file: {path}.");
                if (pe.HasMetadata)
                {
                    var metadata = pe.GetMetadataReader();
                    if (!metadata.IsAssembly)
                        throw new InvalidDataException($"PE file contains CLI metadata but is not a .NET assembly: {path}.");
                    return "managedAssembly";
                }

                if (lower.EndsWith(".dll", StringComparison.Ordinal)) return "nativeRuntime";
                return "applicationHost";
            }
            catch (BadImageFormatException exception)
            {
                throw new InvalidDataException($"Executable image is not a valid PE file: {path}.", exception);
            }
        }
        if (!Path.HasExtension(path)) return "applicationHost";
        return "content";
    }

    internal static void RejectLink(FileSystemInfo info)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Symbolic links and reparse points are not allowed: {info.FullName}");
    }
}

public static class ArtifactValidator
{
    public static ValidationReport Validate(string root, string manifestPath, string policyPath)
    {
        var issues = new List<string>();
        var rootPath = Path.GetFullPath(root);
        var expectedManifestPath = Path.GetFullPath(Path.Combine(rootPath, ArtifactManifestBuilder.ManifestFileName));
        if (!Path.GetFullPath(manifestPath).Equals(expectedManifestPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new(false, ["Manifest must be the reserved core-artifact-manifest.json inside the artifact root."], null, null);
        if (!Directory.Exists(rootPath)) return new(false, ["Artifact root does not exist."], null, null);
        if (!File.Exists(expectedManifestPath)) return new(false, ["Manifest file is missing from the artifact root."], null, null);
        try
        {
            ArtifactManifestBuilder.RejectLink(new DirectoryInfo(rootPath));
            ArtifactManifestBuilder.RejectLink(new FileInfo(expectedManifestPath));
        }
        catch (InvalidDataException exception) { return new(false, [exception.Message], null, null); }
        ArtifactManifest? manifest;
        ArtifactPolicy? policy;
        try { manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(manifestPath), ManifestJson.Options); }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        { return new(false, [$"Manifest could not be read: {exception.GetType().Name}"], null, null); }
        try { policy = JsonSerializer.Deserialize<ArtifactPolicy>(File.ReadAllText(policyPath), ManifestJson.Options); }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        { return new(false, [$"Policy could not be read: {exception.GetType().Name}"], manifest?.ContentId, null); }
        if (manifest is null || policy is null) return new(false, ["Manifest or policy is null."], manifest?.ContentId, policy?.PolicyId);

        if (manifest.Files is null || manifest.Dependencies is null || manifest.Exceptions is null || policy.AllowedDependencies is null ||
            policy.RequiredFiles is null || policy.AllowedFilePatterns is null || policy.ConditionalFilePatterns is null || policy.ExcludedSha256 is null ||
            policy.ForbiddenPathPatterns is null || policy.ForbiddenDependencyPrefixes is null ||
            policy.SupportedTargetFrameworks is null || policy.SupportedRuntimeIdentifiers is null || policy.SupportedPublishModes is null)
            return new(false, ["Manifest or policy is missing a required collection."], manifest.ContentId, policy.PolicyId);

        if (policy.SchemaVersion != 1) issues.Add($"Unsupported policy schema version: {policy.SchemaVersion}.");
        if (manifest.SchemaVersion != 1) issues.Add($"Unsupported manifest schema version: {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.ArtifactId)) issues.Add("artifactId is required.");
        else if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.ArtifactId, "^[a-z0-9][a-z0-9.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            issues.Add("artifactId must be a normalized lowercase identifier.");
        if (string.IsNullOrWhiteSpace(manifest.SourceCommit)) issues.Add("sourceCommit is required.");
        if (!string.IsNullOrWhiteSpace(manifest.SourceCommit) && (manifest.SourceCommit.Length < 7 || manifest.SourceCommit.Any(character => !Uri.IsHexDigit(character))))
            issues.Add("sourceCommit must be a hexadecimal commit identifier of at least seven characters.");
        if (manifest.GeneratedAtUtc == default || manifest.GeneratedAtUtc.Offset != TimeSpan.Zero) issues.Add("generatedAtUtc must be present and UTC.");
        if (manifest.PolicyResult is null || !string.Equals(manifest.PolicyResult.PolicyId, policy.PolicyId, StringComparison.Ordinal) ||
            !string.Equals(manifest.PolicyResult.Status, "passed", StringComparison.Ordinal))
            issues.Add("Manifest policy result is absent, stale, or not passed.");
        if (manifest.Exceptions.Count != 0)
            issues.Add("No policy exceptions are authorized in allowlist v1; exceptions fail closed until separately reviewed and approved.");
        if (!policy.SupportedTargetFrameworks.Contains(manifest.TargetFramework, StringComparer.Ordinal)) issues.Add($"Unsupported target framework: {manifest.TargetFramework}.");
        if (!policy.SupportedRuntimeIdentifiers.Contains(manifest.RuntimeIdentifier, StringComparer.Ordinal)) issues.Add($"Unsupported RID: {manifest.RuntimeIdentifier}.");
        if (!policy.SupportedPublishModes.Contains(manifest.PublishMode, StringComparer.Ordinal)) issues.Add($"Unsupported publish mode: {manifest.PublishMode}.");

        var manifestPaths = new HashSet<string>(StringComparer.Ordinal);
        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path)) { issues.Add("Manifest contains a null or empty file entry."); continue; }
            if (!IsSafeRelativePath(file.Path, out var pathError)) { issues.Add($"Unsafe manifest path '{file.Path}': {pathError}"); continue; }
            var canonical = file.Path.Normalize(NormalizationForm.FormC);
            if (!manifestPaths.Add(canonical)) issues.Add($"Duplicate path: {canonical}.");
            var segments = canonical.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                var prefix = string.Join('/', segments.Take(index + 1));
                if (portablePaths.TryGetValue(prefix, out var existing) && !string.Equals(existing, prefix, StringComparison.Ordinal))
                    issues.Add($"Case-insensitive or portable path collision: {prefix} conflicts with {existing}.");
                else portablePaths[prefix] = prefix;
            }
            if (policy.ForbiddenPathPatterns.Any(pattern => GlobMatches(pattern, canonical))) issues.Add($"Forbidden path: {canonical}.");
            if (!policy.AllowedFilePatterns.Any(pattern => GlobMatches(pattern, canonical)))
            {
                if (policy.ConditionalFilePatterns.Any(pattern => GlobMatches(pattern, canonical))) issues.Add($"File is conditional and not currently authorized: {canonical}.");
                else issues.Add($"File is not allowed by policy: {canonical}.");
            }
            if (policy.ExcludedSha256.Contains(file.Sha256, StringComparer.OrdinalIgnoreCase)) issues.Add($"Excluded rights-sensitive SHA-256: {canonical}.");
            if (file.Size < 0 || !IsSha256(file.Sha256) || file.FileType is not ("applicationHost" or "managedAssembly" or "nativeRuntime" or "dependencyManifest" or "runtimeConfig" or "content"))
            { issues.Add($"Invalid size, SHA-256, or file type: {canonical}."); continue; }
            if (file.Origin is not null && (Path.IsPathRooted(file.Origin) || file.Origin.Contains(':') || file.Origin.Contains('\\') ||
                file.Origin.Split('/').Any(segment => segment is ".." or ".")))
                issues.Add($"File origin must not contain a developer-specific absolute path: {canonical}.");
            var physical = Path.GetFullPath(Path.Combine(rootPath, canonical.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(rootPath, physical)) { issues.Add($"Path escapes artifact root: {canonical}."); continue; }
            VerifyFileType(canonical, physical, file, issues);
            VerifyFile(rootPath, physical, file, issues);
        }

        foreach (var required in policy.RequiredFiles)
        {
            if (!manifestPaths.Contains(required)) issues.Add($"Required file is missing: {required}.");
        }
        ValidateRuntimeSpecificFiles(manifest.RuntimeIdentifier, manifestPaths, issues);
        VerifyDirectoryTree(rootPath, manifestPaths, policy, issues);
        ValidateDependencies(manifest.Dependencies, policy, issues);
        try
        {
            var resolved = ArtifactManifestBuilder.ReadPublishDependencies(rootPath, manifest.Files, manifest.ArtifactId, manifest.TargetFramework, manifest.RuntimeIdentifier, policy);
            var expected = resolved.Select(DependencyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var published = manifest.Dependencies.Where(item => item.Phase is "runtime" or "publish").ToArray();
            var recorded = published.Select(DependencyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var missing in expected.Except(recorded, StringComparer.OrdinalIgnoreCase)) issues.Add($"Manifest omits resolved publish dependency: {missing}.");
            foreach (var extra in recorded.Except(expected, StringComparer.OrdinalIgnoreCase)) issues.Add($"Manifest lists an unresolved publish dependency: {extra}.");
            foreach (var asset in resolved.Where(item => item.Kind == "runtimeAsset"))
            {
                var separator = asset.Id.IndexOf(':');
                var relativeAssetPath = separator < 0 ? string.Empty : asset.Id[(separator + 1)..];
                if (!HasPublishedNativeAsset(relativeAssetPath, manifest.RuntimeIdentifier, manifest.Files))
                    issues.Add($"Resolved native runtime asset is absent from the artifact file list: {relativeAssetPath}.");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        { issues.Add($"Resolved publish dependency graph could not be verified: {exception.GetType().Name}."); }
        var expectedContentId = ManifestJson.ContentId(manifest);
        if (!string.Equals(expectedContentId, manifest.ContentId, StringComparison.OrdinalIgnoreCase)) issues.Add("Content identity does not match canonical manifest content.");

        return new(issues.Count == 0, issues, manifest.ContentId, policy.PolicyId);
    }

    private static void ValidateRuntimeSpecificFiles(string runtimeIdentifier, HashSet<string> manifestPaths, List<string> issues)
    {
        var isWindows = string.Equals(runtimeIdentifier, "win-x64", StringComparison.Ordinal);
        var isLinux = runtimeIdentifier is "linux-x64" or "linux-arm64";
        if (isWindows)
        {
            if (manifestPaths.Contains("Hispania.Core")) issues.Add("Linux Core apphost is not allowed in a win-x64 artifact.");
            if (manifestPaths.Any(path => string.Equals(Path.GetFileName(path), "libe_sqlite3.so", StringComparison.Ordinal)))
                issues.Add("Linux SQLite native asset is not allowed in a win-x64 artifact.");
        }
        else if (isLinux)
        {
            if (manifestPaths.Contains("Hispania.Core.exe")) issues.Add("Windows Core apphost is not allowed in a Linux artifact.");
            if (manifestPaths.Any(path => string.Equals(Path.GetFileName(path), "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)))
                issues.Add("Windows SQLite native asset is not allowed in a Linux artifact.");
        }
    }

    private static void ValidateDependencies(IReadOnlyList<ResolvedDependency> dependencies, ArtifactPolicy policy, List<string> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            var key = $"{dependency.Kind}|{dependency.Id}|{dependency.Version}|{dependency.Phase}";
            if (!seen.Add(key)) issues.Add($"Duplicate resolved dependency: {key}.");
            if (policy.ForbiddenDependencyPrefixes.Any(prefix => dependency.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            { issues.Add($"Forbidden dependency: {dependency.Id} {dependency.Version} ({dependency.Phase})."); continue; }
            var allowed = policy.AllowedDependencies.Any(candidate => candidate.Status == "allowed" &&
                string.Equals(candidate.Kind, dependency.Kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Id, dependency.Id, StringComparison.OrdinalIgnoreCase) &&
                VersionMatches(candidate.Version, dependency.Version) &&
                candidate.Phases.Contains(dependency.Phase, StringComparer.OrdinalIgnoreCase));
            if (!allowed) issues.Add($"Dependency is not explicitly allowlisted: {dependency.Kind} {dependency.Id} {dependency.Version} ({dependency.Phase}).");
            if (dependency.Phase is not ("build" or "runtime" or "publish")) issues.Add($"Unknown dependency phase: {dependency.Phase}.");
            if (string.IsNullOrWhiteSpace(dependency.Version)) issues.Add($"Dependency version is required: {dependency.Id}.");
        }
    }

    private static bool VersionMatches(string allowed, string actual)
    {
        if (string.Equals(allowed, actual, StringComparison.Ordinal)) return true;
        return allowed.EndsWith(".*", StringComparison.Ordinal) && actual.StartsWith(allowed[..^1], StringComparison.Ordinal);
    }

    private static string DependencyKey(ResolvedDependency item) =>
        $"{item.Kind}|{item.Id}|{item.Version}|{item.Phase}|{item.Direct}";

    private static bool HasPublishedNativeAsset(string packageAssetPath, string runtimeIdentifier, IReadOnlyList<ArtifactFile> files)
    {
        if (files.Any(file => string.Equals(file.Path, packageAssetPath, StringComparison.Ordinal))) return true;

        // dotnet publish flattens RID-native package assets into the artifact root. Keep this
        // mapping narrow to runtimes/<selected RID>/native/<single filename>; no basename-only
        // matching is accepted for arbitrary package paths.
        var prefix = $"runtimes/{runtimeIdentifier}/native/";
        if (!packageAssetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = packageAssetPath[prefix.Length..];
        if (fileName.Length == 0 || fileName.Contains('/')) return false;
        return files.Any(file => string.Equals(file.Path, fileName, StringComparison.Ordinal));
    }

    private static void VerifyFile(string root, string path, ArtifactFile expected, List<string> issues)
    {
        try
        {
            var current = new FileInfo(path);
            if (!current.Exists) { issues.Add($"Manifest file is missing: {expected.Path}."); return; }
            ArtifactManifestBuilder.RejectLink(current);
            var parent = current.Directory;
            while (parent is not null && IsWithin(root, parent.FullName))
            {
                ArtifactManifestBuilder.RejectLink(parent);
                if (Path.GetFullPath(parent.FullName).TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
                parent = parent.Parent;
            }
            if (current.Length != expected.Size) issues.Add($"File size mismatch: {expected.Path}.");
            using var stream = current.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase)) issues.Add($"File SHA-256 mismatch: {expected.Path}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { issues.Add($"File verification failed for {expected.Path}: {exception.GetType().Name}."); }
    }

    private static void VerifyFileType(string relativePath, string physicalPath, ArtifactFile expected, List<string> issues)
    {
        if (!File.Exists(physicalPath)) return;
        try
        {
            var actual = ArtifactManifestBuilder.Classify(relativePath, physicalPath);
            if (!string.Equals(actual, expected.FileType, StringComparison.Ordinal))
                issues.Add($"Manifest file type mismatch: {relativePath} declares {expected.FileType}, payload is {actual}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { issues.Add($"File type verification failed for {relativePath}: {exception.Message}"); }
    }

    private static void VerifyDirectoryTree(string root, HashSet<string> manifestPaths, ArtifactPolicy policy, List<string> issues)
    {
        if (!Directory.Exists(root)) return;
        Walk(root, root, manifestPaths, policy, issues);
    }

    private static void Walk(string root, string directory, HashSet<string> manifestPaths, ArtifactPolicy policy, List<string> issues)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { issues.Add($"Cannot enumerate artifact directory: {Path.GetRelativePath(root, directory)} ({exception.GetType().Name})."); return; }
        foreach (var entry in entries.OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { issues.Add($"Symlink/reparse point rejected: {relative}."); continue; }
                if (policy.ForbiddenPathPatterns.Any(pattern => GlobMatches(pattern, relative))) issues.Add($"Forbidden path: {relative}.");
                if ((attributes & FileAttributes.Directory) != 0) { Walk(root, entry, manifestPaths, policy, issues); continue; }
                if (string.Equals(relative, ArtifactManifestBuilder.ManifestFileName, StringComparison.Ordinal)) continue;
                if (!manifestPaths.Contains(relative.Normalize(NormalizationForm.FormC))) issues.Add($"Unexpected artifact file: {relative}.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { issues.Add($"Cannot inspect artifact entry: {relative} ({exception.GetType().Name})."); }
        }
    }

    private static bool IsSafeRelativePath(string path, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(path) || path[0] == '/' || path.Contains('\\') ||
            path.StartsWith("//", StringComparison.Ordinal) || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
        { error = "absolute, empty, or non-normalized path"; return false; }
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")) { error = "empty, dot, or traversal segment"; return false; }
        if (!string.Equals(path, path.Normalize(NormalizationForm.FormC), StringComparison.Ordinal)) { error = "path is not Unicode NFC"; return false; }
        var windowsInvalid = "<>:\"|?*";
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(character => character < 32 || windowsInvalid.Contains(character)) || segment.EndsWith(' ') || segment.EndsWith('.'))
            { error = "invalid or non-portable path character"; return false; }
            var basename = segment.Split('.')[0];
            if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase) || basename.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                basename.Equals("AUX", StringComparison.OrdinalIgnoreCase) || basename.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(basename, @"^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            { error = "reserved Windows device name"; return false; }
        }
        return true;
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool GlobMatches(string pattern, string path)
    {
        var expression = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                { expression.Append("(?:.*/)?"); index += 2; }
                else { expression.Append(".*"); index++; }
            }
            else if (pattern[index] == '*') expression.Append("[^/]*");
            else expression.Append(System.Text.RegularExpressions.Regex.Escape(pattern[index].ToString()));
        }
        expression.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(path, expression.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}

public static class Program
{
    public static int Main(string[] args) => Run(args);

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            var options = Parse(args.Skip(1).ToArray());
            var root = Required(options, "root");
            var policyPath = Required(options, "policy");
            var policy = JsonSerializer.Deserialize<ArtifactPolicy>(File.ReadAllText(policyPath), ManifestJson.Options)
                ?? throw new InvalidDataException("Policy is null.");
            if (args[0] == "create")
            {
                var descriptorPath = Required(options, "descriptor");
                var descriptor = JsonSerializer.Deserialize<ArtifactDescriptor>(File.ReadAllText(descriptorPath), ManifestJson.Options)
                    ?? throw new InvalidDataException("Descriptor is null.");
                var manifest = ArtifactManifestBuilder.Create(root, descriptor, policy) with
                { PolicyResult = new PolicyResult(policy.PolicyId, "passed", []) };
                var output = options.TryGetValue("output", out var specified) ? Path.GetFullPath(specified) : Path.Combine(root, ArtifactManifestBuilder.ManifestFileName);
                var expectedOutput = Path.GetFullPath(Path.Combine(root, ArtifactManifestBuilder.ManifestFileName));
                if (!output.Equals(expectedOutput, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("--output must name core-artifact-manifest.json at the artifact root.");
                File.WriteAllText(output, JsonSerializer.Serialize(manifest, ManifestJson.Options) + Environment.NewLine, new UTF8Encoding(false));
                var report = ArtifactValidator.Validate(root, output, policyPath);
                manifest = manifest with { PolicyResult = new PolicyResult(policy.PolicyId, report.Passed ? "passed" : "failed", report.Issues) };
                File.WriteAllText(output, JsonSerializer.Serialize(manifest, ManifestJson.Options) + Environment.NewLine, new UTF8Encoding(false));
                Console.WriteLine(JsonSerializer.Serialize(report, ManifestJson.Options));
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "validate")
            {
                var manifestPath = options.TryGetValue("manifest", out var specified) ? specified : Path.Combine(root, ArtifactManifestBuilder.ManifestFileName);
                var result = ArtifactValidator.Validate(root, manifestPath, policyPath);
                Console.WriteLine(JsonSerializer.Serialize(result, ManifestJson.Options));
                return result.Passed ? 0 : 1;
            }
            return Usage();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new ValidationReport(false, [$"{exception.GetType().Name}: {exception.Message}"], null, null), ManifestJson.Options));
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: create --root DIR --descriptor FILE --policy FILE [--output FILE] | validate --root DIR --policy FILE [--manifest FILE]");
        return 2;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Options require --name value pairs.");
            if (!result.TryAdd(args[index][2..], args[index + 1])) throw new ArgumentException($"Duplicate option: {args[index]}.");
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{name}.");
}
