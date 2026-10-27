using Hispania.Runtime;
using Microsoft.Extensions.Logging;

namespace Hispania.Core.Modules;

internal sealed class CoreModuleManager(ModuleExtensionCatalog catalog, string dataDirectory)
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private static readonly EventId PackageInstallRejected = new(30, "ModulePackageInstallRejected");
    private static readonly Action<ILogger, string, string, string, string, string, string?, Exception?> LogPackageInstallRejected =
        LoggerMessage.Define<string, string, string, string, string, string?>(LogLevel.Warning, PackageInstallRejected,
            "Module package install rejected. DiagnosticId {DiagnosticId}; Stage {Stage}; Result {Result}; ExceptionType {ExceptionType}; FindingCodes {FindingCodes}; PublisherTrustStatus {PublisherTrustStatus}.");
    private static readonly HashSet<string> SafeFindingCodes = new(StringComparer.Ordinal)
    {
        "GraphLimit", "InvalidManifest", "AmbiguousArtifact", "MissingDependency", "VersionConflict",
        "CycleOrDepth", "ProtocolIncompatible", "HostIncompatible", "UntrustedDependency", "PublisherOwnership"
    };

    public IReadOnlyList<PackageGraphDiagnostic> List() => catalog.InspectPackageGraph();

    public async Task<string> InstallAsync(Stream source, CancellationToken cancellationToken,
        ILogger? diagnosticLogger = null, string? diagnosticId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        diagnosticId ??= Guid.NewGuid().ToString("N");
        var stage = "archive_inspection";
        var findingCodes = string.Empty;
        string? publisherTrustStatus = null;
        var staging = Path.Combine(dataDirectory, "module-staging");
        var archivePath = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".hispania");
        try
        {
            try
            {
                StoragePathGuard.RequireWithin(dataDirectory, staging, "Module staging escaped DATA_DIRECTORY.");
                StoragePathGuard.RequireOutside(staging, AppContext.BaseDirectory, "Module staging overlaps the Core publish directory.");
                Directory.CreateDirectory(staging);
                StoragePathGuard.RequireWithin(dataDirectory, staging, "Module staging escaped DATA_DIRECTORY.");
                StoragePathGuard.RequireOutside(staging, AppContext.BaseDirectory, "Module staging overlaps the Core publish directory.");

                await using (var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total = checked(total + read);
                        if (total > MaximumPackageBytes) throw new InvalidDataException("Module package exceeds the upload limit.");
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var candidate = await PackageArchiveInspector.InspectAsync(archivePath, cancellationToken).ConfigureAwait(false);
                stage = "host_runtime_policy";
                catalog.ValidateManagedModuleRuntime(candidate.Manifest);
                var targets = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [candidate.Id] = $"[{candidate.Version}]"
                };
                stage = "package_plan";
                var plan = await catalog.PlanPackageSetAsync([archivePath], targets, cancellationToken: cancellationToken).ConfigureAwait(false);
                findingCodes = string.Join(',', plan.Findings.Select(finding => finding.Code)
                    .Where(SafeFindingCodes.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                if (!plan.Resolved)
                    throw new InvalidDataException("Package trust, integrity, compatibility, or dependency policy rejected the package.");
                var selectedFixture = plan.Selected.Any(package => package.Id == "hispania.slice7.fixture" && package.ArtifactHash == candidate.ArtifactHash);
                if (selectedFixture) publisherTrustStatus = "Trusted";
                stage = "publisher_id_policy";
                if (!string.Equals(candidate.Manifest.PublisherId, "hispania.slice7.test", StringComparison.Ordinal))
                    throw new InvalidDataException("Slice 7 fixture must be signed by the explicitly configured test publisher.");
                if (!selectedFixture)
                {
                    stage = "package_plan";
                    throw new InvalidDataException("Only the approved Slice 7 fixture package may be installed by Core v1.0.");
                }

                stage = "package_apply";
                await catalog.ApplyPackageSetAsync(plan, new HashSet<string>(StringComparer.Ordinal), cancellationToken: cancellationToken).ConfigureAwait(false);
                return candidate.Id;
            }
            finally
            {
                StoragePathGuard.EnsureNoReparsePoints(archivePath);
                if (File.Exists(archivePath)) File.Delete(archivePath);
            }
        }
        catch (Exception exception) when (IsInstallRejection(exception))
        {
            if (diagnosticLogger is not null)
                LogPackageInstallRejected(diagnosticLogger, diagnosticId, stage, "rejected", ClassifyException(exception), findingCodes,
                    publisherTrustStatus, null);
            throw;
        }
    }

    private static bool IsInstallRejection(Exception exception) => exception is ModuleRuntimePolicyException or
        InvalidDataException or InvalidOperationException or IOException or ArgumentException;

    private static string ClassifyException(Exception exception) => exception switch
    {
        ModuleRuntimePolicyException => "ModuleRuntimePolicyException",
        InvalidDataException => "InvalidDataException",
        InvalidOperationException => "InvalidOperationException",
        IOException => "IOException",
        ArgumentException => "ArgumentException",
        _ => "OtherExpectedException"
    };

    public Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) =>
        catalog.SetPackageEnabledAsync(id, enabled, cancellationToken: cancellationToken);

    public Task RemoveAsync(string id, CancellationToken cancellationToken) =>
        catalog.SetPackageEnabledAsync(id, enabled: false, uninstall: true, cancellationToken);
}
