using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hispania.Hosting;

public sealed class ProductionConfiguration
{
    public const int SchemaGeneration = 1;
    public int Generation { get; set; } = SchemaGeneration;
    public string Role { get; set; } = "server";
    public string Endpoint { get; set; } = "http://127.0.0.1:5080";
    public string WebBuild { get; set; } = "modern";
    public string LogLevel { get; set; } = "Information";
    public string? BootstrapSecret { get; set; }
    public string? BootstrapUsername { get; set; }
    public string? BootstrapPasswordSecret { get; set; }
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";
    public string Persistence { get; set; } = "sqlite";
    public PostgreSqlConfiguration PostgreSql { get; set; } = new();
    public TlsConfiguration Tls { get; set; } = new();
    public WorkerConfiguration Worker { get; set; } = new();
    public CapacityConfiguration Capacity { get; set; } = new();
    public MaintenanceConfiguration Maintenance { get; set; } = new();
    public string[] LibraryRoots { get; set; } = [];
    public string[] TrustedExtensionIds { get; set; } = [];
    public Dictionary<string, string> TrustedPublisherKeys { get; set; } = new(StringComparer.Ordinal);
    public string[] RevokedPublisherIds { get; set; } = [];

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public void Validate(string expectedRole)
    {
        Require(PostgreSql is not null && Tls is not null && Worker is not null && Capacity is not null && Maintenance is not null && LibraryRoots is not null && TrustedExtensionIds is not null && TrustedPublisherKeys is not null && RevokedPublisherIds is not null, "objects", "configuration objects and arrays cannot be null");
        Require(Capacity.ReserveBytes >= 0 && double.IsFinite(Capacity.WarningFreeFraction) && double.IsFinite(Capacity.CriticalFreeFraction) &&
            Capacity.CriticalFreeFraction > 0 && Capacity.CriticalFreeFraction < Capacity.WarningFreeFraction && Capacity.WarningFreeFraction < 1,
            "capacity", "reserve and warning/critical fractions must be coherent");
        Require(Maintenance.JobIdempotencyHorizonDays is >= 1 and <= 3650 && Maintenance.OrphanGraceHours is >= 1 and <= 720,
            "maintenance", "idempotency horizon and orphan grace are outside supported bounds");
        if (Maintenance.BackupArchiveRoot is not null)
            Require(Path.IsPathFullyQualified(Maintenance.BackupArchiveRoot), "maintenance.backupArchiveRoot", "use an absolute archive path outside the installation state root");
        Require(Worker.CoordinatorCertificateSha256Pins is not null && Worker.ContractAssemblies is not null && Worker.CapabilityProviderAssemblies is not null, "worker arrays", "arrays cannot be null");
        Require(Generation == SchemaGeneration, "generation", "unsupported schema; expected 1");
        Require(Role == expectedRole, "role", "must match the host executable");
        Require(WebBuild is "modern" or "classic", "webBuild", "use modern or classic");
        Require(LogLevel is "Information" or "Warning" or "Error" or "Critical", "logLevel", "unsupported production log level");
        Require(Persistence is "sqlite" or "postgresql", "persistence", "use sqlite or postgresql");
        Require(Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https" && endpoint.Port > 0 && endpoint.Port <= 65535 && endpoint.AbsolutePath == "/" && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0,
            "endpoint", "use an explicit http(s) origin with a fixed port");
        Require(endpoint!.Scheme == "https" || endpoint.IsLoopback, "endpoint", "HTTP is restricted to loopback; provide TLS for remote binding");
        if (endpoint.Scheme == "https") Require(Tls.CertificateFile is not null, "tls.certificateFile", "HTTPS requires private certificate material");
        Require(PostgreSql.Port is >= 1 and <= 65535, "postgreSql.port", "range 1..65535");
        if (Persistence == "postgresql")
        {
            Require(!string.IsNullOrWhiteSpace(PostgreSql.Host) && !string.IsNullOrWhiteSpace(PostgreSql.Database) && !string.IsNullOrWhiteSpace(PostgreSql.Username), "postgreSql", "host, database and username are required");
            Require(PostgreSql.PasswordSecret is not null, "postgreSql.passwordSecret", "a secret reference is required");
        }
        Require(Worker.MaximumConcurrentJobs is >= 1 and <= 64, "worker.maximumConcurrentJobs", "range 1..64");
        Require(Worker.LeaseSeconds is >= 5 and <= 300, "worker.leaseSeconds", "range 5..300");
        Require(Worker.PollMilliseconds is >= 50 and <= 60000, "worker.pollMilliseconds", "range 50..60000");
        Require(Worker.HeartbeatSeconds is >= 1 and <= 300 && Worker.HeartbeatSeconds < Worker.LeaseSeconds, "worker.heartbeatSeconds", "range 1..300, below leaseSeconds");
        if (Role == "worker")
        {
            Require(Uri.TryCreate(Worker.CoordinatorUri, UriKind.Absolute, out var coordinator) && coordinator.Scheme == "https" && coordinator.UserInfo.Length == 0, "worker.coordinatorUri", "an HTTPS coordinator is required");
            Require(!string.IsNullOrWhiteSpace(Worker.NodeId) && Worker.NodeId.Length <= 128, "worker.nodeId", "a bounded node identity is required");
            Require(Worker.CertificateFile is not null, "worker.certificateFile", "provision a private client certificate");
            Require(Worker.CoordinatorCertificateSha256Pins.Length > 0 && Worker.CoordinatorCertificateSha256Pins.All(pin => pin is not null && pin.Length == 64 && pin.All(Uri.IsHexDigit)), "worker.coordinatorCertificateSha256Pins", "explicit SHA-256 pins are required");
        }
        foreach (var path in LibraryRoots) Require(path is not null && Path.IsPathFullyQualified(path), "libraryRoots", "paths must be absolute");
        foreach (var path in Worker.CapabilityProviderAssemblies)
            Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':', StringComparison.Ordinal) && !path.Split('/', '\\').Any(part => part is ".." or ".") && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), "worker.capabilityProviderAssemblies", "use artifact-relative DLL paths");
        foreach (var reference in new[] { BootstrapSecret, BootstrapPasswordSecret, PostgreSql.PasswordSecret, Tls.PasswordSecret, Worker.PasswordSecret })
            if (reference is not null) InstallationSecrets.ValidateReference(reference);
        foreach (var path in new[] { Tls.CertificateFile, Worker.CertificateFile })
            if (path is not null) Require(!Path.IsPathRooted(path) && !path.Contains(':', StringComparison.Ordinal) && !path.Split('/', '\\').Any(part => part is ".." or ".") && !string.IsNullOrWhiteSpace(path), "certificateFile", "use a path within secrets/");
        foreach (var key in TrustedPublisherKeys.Values)
            Require(key is not null && !key.Contains("PRIVATE KEY", StringComparison.Ordinal), "trustedPublisherKeys", "only public trust material belongs in configuration");
    }

    internal static void Require([DoesNotReturnIf(false)] bool condition, string field, string action)
    {
        if (!condition) throw new DeploymentException(2, $"Invalid configuration: {field}; {action}.");
    }

    internal static ProductionConfiguration Read(string path, IEnumerable<KeyValuePair<string, string?>> overrides)
    {
        try
        {
            var defaults = JsonSerializer.SerializeToNode(new ProductionConfiguration(), Json)!.AsObject();
            if (new FileInfo(path).Length > 1024 * 1024) throw new JsonException();
            var file = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new JsonException();
            Merge(defaults, file);
            foreach (var (key, value) in overrides)
            {
                var parts = key.Split("__", StringSplitOptions.None);
                JsonObject target = defaults;
                for (var i = 0; i < parts.Length; i++)
                {
                    var field = target.Select(entry => entry.Key).SingleOrDefault(name => name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                    Require(field is not null, "override", "unknown field; consult configuration reference");
                    if (i < parts.Length - 1)
                    {
                        Require(target[field!] is JsonObject, "override", "field is not an object");
                        target = target[field!]!.AsObject();
                    }
                    else
                    {
                        var current = target[field!];
                        target[field!] = current is JsonArray or JsonObject || current is JsonValue scalar && !scalar.TryGetValue<string>(out _)
                            ? JsonNode.Parse(value ?? "null") : JsonValue.Create(value);
                    }
                }
            }
            return defaults.Deserialize<ProductionConfiguration>(Json) ?? throw new JsonException();
        }
        catch (DeploymentException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw new DeploymentException(2, "Invalid configuration: config/production.json; check file access, known field names and JSON types."); }
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject nested && target[key] is JsonObject existing) Merge(existing, nested);
            else target[key] = value?.DeepClone();
        }
    }
}

public sealed class PostgreSqlConfiguration
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "hispania";
    public string Username { get; set; } = "hispania";
    public string? PasswordSecret { get; set; }
}

public sealed class TlsConfiguration
{
    public string? CertificateFile { get; set; }
    public string? PasswordSecret { get; set; }
}

public sealed class WorkerConfiguration
{
    public bool FfmpegEnabled { get; set; }
    public string? CoordinatorUri { get; set; }
    public string? NodeId { get; set; }
    public string? CertificateFile { get; set; }
    public string? PasswordSecret { get; set; }
    public string[] CoordinatorCertificateSha256Pins { get; set; } = [];
    public string[] ContractAssemblies { get; set; } = [];
    public string[] CapabilityProviderAssemblies { get; set; } = [];
    public int MaximumConcurrentJobs { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 30;
    public int PollMilliseconds { get; set; } = 500;
    public int HeartbeatSeconds { get; set; } = 5;
}

public sealed class CapacityConfiguration
{
    public long ReserveBytes { get; set; } = 1024L * 1024 * 1024;
    public double WarningFreeFraction { get; set; } = 0.15;
    public double CriticalFreeFraction { get; set; } = 0.05;
}

public sealed class MaintenanceConfiguration
{
    public int JobIdempotencyHorizonDays { get; set; } = 365;
    public int OrphanGraceHours { get; set; } = 24;
    public string? BackupArchiveRoot { get; set; }
}

public sealed class DeploymentException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
