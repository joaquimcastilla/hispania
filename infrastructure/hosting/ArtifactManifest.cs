using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hispania.Hosting;

public sealed class ArtifactManifest
{
    public int ManifestGeneration { get; set; } = 1;
    public string DistributionVersion { get; set; } = "0.7.0";
    public string GitSha { get; set; } = "";
    public bool SourceDirty { get; set; }
    public string TargetRid { get; set; } = "";
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
            if (manifest.ManifestGeneration != 1 || !IsSemVer(manifest.DistributionVersion) || manifest.GitSha.Length != 40 || !manifest.GitSha.All(Uri.IsHexDigit) || manifest.TargetRid is not ("win-x64" or "linux-x64") || manifest.BuildConfiguration != "Release" || manifest.BuildUtc == default) throw new InvalidDataException();
            stage = "file inventory";
            var files = Directory.EnumerateFiles(artifact, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(artifact, file).Replace('\\', '/')).Where(file => file != "artifact-manifest.json").Order(StringComparer.Ordinal).ToArray();
            if (!files.SequenceEqual(manifest.Files.Keys.Order(StringComparer.Ordinal))) throw new InvalidDataException();
            foreach (var (relative, expected) in manifest.Files)
            {
                stage = "file checksum or path safety";
                if (Path.IsPathFullyQualified(relative) || relative.Split('/').Any(part => part is ".." or ".")) throw new InvalidDataException();
                var file = Path.Combine(artifact, relative);
                ProductionInstallation.RejectLinks(Path.GetDirectoryName(file)!);
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                using var stream = File.OpenRead(file);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            }
            foreach (var required in new[] { "server/Hispania.Server.dll", "server/module-host/Hispania.ModuleHost.dll", "server/ffmpeg-worker/Hispania.FFmpeg.Worker.dll", "server/wwwroot/modern/index.html", "server/wwwroot/classic/index.html", "worker/Hispania.WorkerAgent.dll", "worker/module-host/Hispania.ModuleHost.dll" })
                if (!manifest.Files.ContainsKey(required)) throw new InvalidDataException();
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { throw new DeploymentException(5, $"Artifact integrity validation failed ({stage}); supply a complete trusted publication and verify its external checksum."); }
    }

    private static bool IsSemVer(string value) => Regex.IsMatch(value,
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant);
}
