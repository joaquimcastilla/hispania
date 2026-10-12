using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hispania.Hosting;

public sealed class ArtifactManifest
{
    public int ManifestGeneration { get; set; } = 1;
    public string DistributionVersion { get; set; } = "";
    public string DistributionType { get; set; } = "complete";
    public string GitSha { get; set; } = "";
    public string SourceBranch { get; set; } = "unknown";
    public bool SourceDirty { get; set; }
    public string TargetRid { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string PublishProfile { get; set; } = "complete-production";
    public string BuildConfiguration { get; set; } = "Release";
    public string DotnetSdkVersion { get; set; } = "";
    public string RuntimePrerequisite { get; set; } = "ASP.NET Core Runtime 10.0 (x64)";
    public DateTimeOffset BuildUtc { get; set; }
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    public static ArtifactManifest Verify(string artifact)
    {
        var stage = "directory safety";
        try
        {
            ProductionInstallation.RejectLinks(artifact);
            var directories = new Stack<string>(); directories.Push(artifact);
            while (directories.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
            stage = "manifest parsing and metadata";
            var path = Path.Combine(artifact, "artifact-manifest.json");
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException();
            var manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(path), ProductionConfiguration.Json) ?? throw new InvalidDataException();
            if (manifest.ManifestGeneration != 1 || manifest.DistributionVersion is null || !IsSemVer(manifest.DistributionVersion) || manifest.GitSha.Length != 40 || !manifest.GitSha.All(Uri.IsHexDigit) || manifest.TargetRid is not ("win-x64" or "linux-x64" or "linux-arm64") || manifest.DistributionType is not ("complete" or "platform-only") || manifest.BuildConfiguration != "Release" || manifest.BuildUtc == default) throw new InvalidDataException();
            stage = "file inventory";
            var files = Directory.EnumerateFiles(artifact, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(artifact, file).Replace('\\', '/')).Where(file => file != "artifact-manifest.json").Order(StringComparer.Ordinal).ToArray();
            if (!files.SequenceEqual(manifest.Files.Keys.Order(StringComparer.Ordinal))) throw new InvalidDataException();
            foreach (var (relative, expected) in manifest.Files)
            {
                stage = "file path validation";
                if (Path.IsPathFullyQualified(relative) || relative.Split('/').Any(part => part is ".." or ".")) throw new InvalidDataException();
                var file = Path.Combine(artifact, relative);
                stage = "file link validation: " + relative;
                ProductionInstallation.RejectLinks(Path.GetDirectoryName(file)!);
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                using var stream = File.OpenRead(file);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                stage = "file checksum: " + relative + " expected=" + expected + " actual=" + actual;
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            }
            var requiredFiles = manifest.DistributionType == "platform-only"
                ? new[] { "server/Hispania.Platform.Server.dll", "server/Hispania.Platform.Server.deps.json", "server/module-host/Hispania.ModuleHost.dll", "deployment/Hispania.Deployment.dll" }
                : new[] { "server/Hispania.Server.dll", "server/module-host/Hispania.ModuleHost.dll", "server/ffmpeg-worker/Hispania.FFmpeg.Worker.dll", "server/wwwroot/modern/index.html", "server/wwwroot/classic/index.html", "worker/Hispania.WorkerAgent.dll", "worker/module-host/Hispania.ModuleHost.dll" };
            foreach (var required in requiredFiles)
            {
                stage = "required file: " + required;
                if (!manifest.Files.ContainsKey(required))
                {
                    var similar = manifest.Files.Keys.Where(file => file.Contains(Path.GetFileName(required), StringComparison.OrdinalIgnoreCase)).ToArray();
                    stage += "; related manifest paths=" + string.Join(",", similar);
                    throw new InvalidDataException();
                }
            }
            if (manifest.DistributionType == "platform-only")
            {
                stage = "platform architecture metadata";
                if (manifest.TargetRid == "win-x64" && manifest.Architecture is not ("x86_64" or "")) throw new InvalidDataException();
                if (manifest.TargetRid == "linux-x64" && manifest.Architecture is not ("x86_64" or "")) throw new InvalidDataException();
                if (manifest.TargetRid == "linux-arm64" && manifest.Architecture is not ("aarch64" or "")) throw new InvalidDataException();
                stage = "platform-only filename exclusion";
                var excludedFile = files.FirstOrDefault(file => System.Text.RegularExpressions.Regex.IsMatch(file, @"Hispania\.(Media|Inventory|Library|Playback|Transcoding|Metadata|FFmpeg)|WindowsServices|ServiceController", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
                if (excludedFile is not null) { stage += ": " + excludedFile; throw new InvalidDataException(); }
                stage = "platform-only dependency exclusion: server/Hispania.Platform.Server.deps.json";
                var deps = File.ReadAllText(Path.Combine(artifact, "server", "Hispania.Platform.Server.deps.json"));
                var excludedDependency = System.Text.RegularExpressions.Regex.Match(deps, @"Hispania\.(Media|Inventory|Library|Playback|Transcoding|Metadata|FFmpeg)|WindowsServices|ServiceController", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                if (excludedDependency.Success) { stage += ": " + excludedDependency.Value; throw new InvalidDataException(); }
            }
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { throw new DeploymentException(5, $"Artifact integrity validation failed ({stage}); supply a complete trusted publication and verify its external checksum."); }
    }

    private static bool IsSemVer(string? value) => value is not null && Regex.IsMatch(value,
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant);
}
