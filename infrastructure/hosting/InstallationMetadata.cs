using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Hispania.Hosting;

internal static class InstallationMetadata
{
    private sealed record Record(string DistributionVersion, string GitSha, string TargetRid, DateTimeOffset InstalledUtc, DateTimeOffset UpdatedUtc, string ManifestSha256);
    private static string PathFor(string home) => Path.Combine(home, "config", "installation.json");
    private static string Digest(string app)
    {
        using var stream = File.OpenRead(Path.Combine(app, "artifact-manifest.json")); return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static void Write(string home)
    {
        var app = Path.Combine(home, "app"); var artifact = ArtifactManifest.Verify(app);
        var path = PathFor(home); var now = DateTimeOffset.UtcNow;
        var installed = File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path), ProductionConfiguration.Json)!.InstalledUtc : now;
        var record = new Record(artifact.DistributionVersion, artifact.GitSha, artifact.TargetRid, installed, now, Digest(app));
        File.WriteAllText(path + ".pending", JsonSerializer.Serialize(record, ProductionConfiguration.Json)); File.Move(path + ".pending", path, true);
    }
    internal static void Validate(string home, string role)
    {
        var stage = "application directory binding";
        try
        {
            var app = Path.Combine(home, "app");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(Path.Combine(app, role == "server" ? "server" : "worker"), comparison)) throw new InvalidDataException();
            stage = "installation record parsing";
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(PathFor(home)), ProductionConfiguration.Json) ?? throw new InvalidDataException();
            stage = "artifact manifest parsing";
            var artifact = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(Path.Combine(app, "artifact-manifest.json")), ProductionConfiguration.Json) ?? throw new InvalidDataException();
            stage = "installation record and manifest binding";
            if (record.DistributionVersion != artifact.DistributionVersion || record.GitSha != artifact.GitSha || record.TargetRid != artifact.TargetRid || record.ManifestSha256 != Digest(app) || record.InstalledUtc == default) throw new InvalidDataException();
            var entry = role == "server"
                ? artifact.DistributionType == "platform-only" ? "server/Hispania.Platform.Server.dll" : "server/Hispania.Server.dll"
                : "worker/Hispania.WorkerAgent.dll";
            stage = "installed entry assembly checksum";
            using var executable = File.OpenRead(Path.Combine(app, entry));
            if (!artifact.Files.TryGetValue(entry, out var expected) || !Convert.ToHexString(SHA256.HashData(executable)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            executable.Position = 0;
            stage = "loaded entry assembly identity";
            using var image = new PEReader(executable);
            var metadata = image.GetMetadataReader();
            if (System.Reflection.Assembly.GetEntryAssembly()!.ManifestModule.ModuleVersionId != metadata.GetGuid(metadata.GetModuleDefinition().Mvid)) throw new InvalidDataException();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { throw new DeploymentException(6, $"Installed artifact and durable installation metadata differ ({stage}); inspect verified upgrade/recovery evidence before starting. Run the host from HOME/app."); }
    }
}
