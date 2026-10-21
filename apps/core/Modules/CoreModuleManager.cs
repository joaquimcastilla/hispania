using Hispania.Runtime;

namespace Hispania.Core.Modules;

internal sealed class CoreModuleManager(ModuleExtensionCatalog catalog, string dataDirectory)
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;

    public IReadOnlyList<PackageGraphDiagnostic> List() => catalog.InspectPackageGraph();

    public async Task<string> InstallAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var staging = Path.Combine(dataDirectory, "module-staging");
        StoragePathGuard.RequireWithin(dataDirectory, staging, "Module staging escaped DATA_DIRECTORY.");
        StoragePathGuard.RequireOutside(staging, AppContext.BaseDirectory, "Module staging overlaps the Core publish directory.");
        Directory.CreateDirectory(staging);
        StoragePathGuard.RequireWithin(dataDirectory, staging, "Module staging escaped DATA_DIRECTORY.");
        StoragePathGuard.RequireOutside(staging, AppContext.BaseDirectory, "Module staging overlaps the Core publish directory.");

        var archivePath = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".hispania");
        try
        {
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
            catalog.ValidateManagedModuleRuntime(candidate.Manifest);
            var targets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [candidate.Id] = $"[{candidate.Version}]"
            };
            var plan = await catalog.PlanPackageSetAsync([archivePath], targets, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!plan.Resolved)
                throw new InvalidDataException("Package trust, integrity, compatibility, or dependency policy rejected the package.");
            if (!string.Equals(candidate.Manifest.PublisherId, "hispania.slice7.test", StringComparison.Ordinal))
                throw new InvalidDataException("Slice 7 fixture must be signed by the explicitly configured test publisher.");
            if (!plan.Selected.Any(package => package.Id == "hispania.slice7.fixture" && package.ArtifactHash == candidate.ArtifactHash))
                throw new InvalidDataException("Only the approved Slice 7 fixture package may be installed by Core v1.0.");

            await catalog.ApplyPackageSetAsync(plan, new HashSet<string>(StringComparer.Ordinal), cancellationToken: cancellationToken).ConfigureAwait(false);
            return candidate.Id;
        }
        finally
        {
            StoragePathGuard.EnsureNoReparsePoints(archivePath);
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }
    }

    public Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) =>
        catalog.SetPackageEnabledAsync(id, enabled, cancellationToken: cancellationToken);

    public Task RemoveAsync(string id, CancellationToken cancellationToken) =>
        catalog.SetPackageEnabledAsync(id, enabled: false, uninstall: true, cancellationToken);
}
