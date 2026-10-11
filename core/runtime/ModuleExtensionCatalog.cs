using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Jobs;

namespace Hispania.Runtime;

public sealed record DiscoveredExtension(ModuleManifest Manifest, string ManifestPath, bool Compatible, string? RejectionReason, bool Trusted, ModuleLifecycleState? State)
{
    public bool Installed { get; init; }
    public bool Loaded { get; init; }
    public PublisherSignatureStatus PublisherStatus { get; init; }
}

/// <summary>Discovers inert manifests and activates only host-allowlisted packages/factories.</summary>
public sealed partial class ModuleExtensionCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string directory;
    private readonly Version hostVersion;
    private readonly IModuleRuntime runtime;
    private readonly IModuleRegistry registry;
    private readonly IReadOnlyDictionary<string, Func<IModule>> trustedFactories;
    private readonly HashSet<string> allowlist;
    private readonly ExtensionPackageInstaller installer;
    private readonly string moduleHostPath;
    private readonly Dictionary<string, string> trustedPublisherKeys;
    private readonly HashSet<string> revokedPublisherIds;
    private readonly object publisherTrustGate = new();
    private readonly bool requirePublisherSignature;
    private readonly OutOfProcessResourceLimits resourceLimits;
    private readonly OutOfProcessRestartPolicy restartPolicy;
    private readonly Hispania.Platform.Contracts.Audit.IPlatformAuditWriter? packageAuditWriter;
    private readonly IPlatformJobContributionRegistryV1? jobContributionRegistry;
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim activationGate = new(1, 1);
    private readonly Dictionary<string, DiscoveredExtension> discovered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedExtension> loaded = new(StringComparer.Ordinal);

    // Preserve the predecessor CLR constructor signature for already compiled host composition consumers.
    public ModuleExtensionCatalog(string directory, Version hostVersion, IModuleRuntime runtime, IModuleRegistry registry,
        IReadOnlyDictionary<string, Func<IModule>> trustedFactories, IEnumerable<string>? allowedExtensionIds, string? moduleHostPath,
        IReadOnlyDictionary<string, string>? trustedPublisherKeys, IEnumerable<string>? revokedPublisherIds, bool requirePublisherSignature,
        OutOfProcessResourceLimits? resourceLimits, OutOfProcessRestartPolicy? restartPolicy)
        : this(directory, hostVersion, runtime, registry, trustedFactories, allowedExtensionIds, moduleHostPath, trustedPublisherKeys,
            revokedPublisherIds, requirePublisherSignature, resourceLimits, restartPolicy, null) { }

    public ModuleExtensionCatalog(string directory, Version hostVersion, IModuleRuntime runtime, IModuleRegistry registry, IReadOnlyDictionary<string, Func<IModule>> trustedFactories, IEnumerable<string>? allowedExtensionIds = null, string? moduleHostPath = null, IReadOnlyDictionary<string, string>? trustedPublisherKeys = null, IEnumerable<string>? revokedPublisherIds = null, bool requirePublisherSignature = false, OutOfProcessResourceLimits? resourceLimits = null, OutOfProcessRestartPolicy? restartPolicy = null, Hispania.Platform.Contracts.Audit.IPlatformAuditWriter? packageAuditWriter = null)
        : this(directory, hostVersion, runtime, registry, trustedFactories, allowedExtensionIds, moduleHostPath, trustedPublisherKeys, revokedPublisherIds, requirePublisherSignature, resourceLimits, restartPolicy, packageAuditWriter, null) { }

    public ModuleExtensionCatalog(string directory, Version hostVersion, IModuleRuntime runtime, IModuleRegistry registry, IReadOnlyDictionary<string, Func<IModule>> trustedFactories, IEnumerable<string>? allowedExtensionIds, string? moduleHostPath, IReadOnlyDictionary<string, string>? trustedPublisherKeys, IEnumerable<string>? revokedPublisherIds, bool requirePublisherSignature, OutOfProcessResourceLimits? resourceLimits, OutOfProcessRestartPolicy? restartPolicy, Hispania.Platform.Contracts.Audit.IPlatformAuditWriter? packageAuditWriter, IPlatformJobContributionRegistryV1? jobContributionRegistry)
    {
        this.directory = Path.GetFullPath(directory);
        this.hostVersion = hostVersion;
        this.runtime = runtime;
        this.packageAuditWriter = packageAuditWriter;
        this.jobContributionRegistry = jobContributionRegistry;
        this.registry = registry;
        this.moduleHostPath = Path.GetFullPath(moduleHostPath ?? Path.Combine(AppContext.BaseDirectory, "module-host", "Hispania.ModuleHost.dll"));
        this.trustedPublisherKeys = new Dictionary<string, string>(trustedPublisherKeys ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        this.revokedPublisherIds = new HashSet<string>(revokedPublisherIds ?? [], StringComparer.Ordinal);
        this.requirePublisherSignature = requirePublisherSignature;
        this.resourceLimits = resourceLimits ?? new OutOfProcessResourceLimits();
        this.resourceLimits.Validate();
        this.restartPolicy = restartPolicy ?? new OutOfProcessRestartPolicy();
        this.restartPolicy.Validate();
        this.trustedFactories = trustedFactories;
        allowlist = new HashSet<string>(trustedFactories.Keys, StringComparer.Ordinal);
        if (allowedExtensionIds is not null) allowlist.UnionWith(allowedExtensionIds);
        installer = new ExtensionPackageInstaller(this.directory, hostVersion, allowlist, manifest => manifest.PackageFormatVersion == 2
            ? "Format 2 requires a planned package-set operation." : ValidateContracts(manifest));
    }

    public async Task<InstalledExtensionPackage> InstallAsync(Stream package, CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoIncompleteOperation();
            var installed = await installer.InstallAsync(package, cancellationToken).ConfigureAwait(false);
            _ = Discover();
            return installed;
        }
        finally { activationGate.Release(); }
    }

    public async Task<bool> UninstallAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            RejectManagedMutation(id);
            if (Discover().Any(extension => extension.Manifest.Id != id && PackageDependencyResolver.Dependencies(extension.Manifest).Any(dependency => dependency.PackageId == id)))
                throw new InvalidOperationException("An installed dependent blocks uninstall.");
            if (!allowlist.Contains(id)) throw new InvalidOperationException("Extension is not in the host allowlist.");
            if (!Version.TryParse(version, out var parsed)) throw new ArgumentException("Invalid extension version.", nameof(version));
            var packageDirectory = Path.GetFullPath(Path.Combine(directory, id, parsed.ToString()));
            var expectedRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!packageDirectory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Package path is outside the extension directory.");
            lock (syncRoot)
            {
                if (loaded.TryGetValue(id, out var active) && string.Equals(Path.GetFullPath(active.PackageDirectory), packageDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unload the extension before uninstalling its package.");
                var registered = registry.List().FirstOrDefault(module => module.Descriptor.Id == id);
                if (registered?.Descriptor.Version == parsed) throw new InvalidOperationException("Unload the registered module before uninstalling its version.");
            }
            if (!Directory.Exists(packageDirectory)) return false;
            if ((File.GetAttributes(Path.Combine(directory, id)) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(packageDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to uninstall through a reparse point.");
            Directory.Delete(packageDirectory, recursive: true);
            return true;
        }
        finally { activationGate.Release(); }
    }

    public IReadOnlyCollection<DiscoveredExtension> Discover()
    {
        Directory.CreateDirectory(directory);
        var committed = ReadState<PackageSetState>(PackageCatalogPath);
        if (committed is not null) ValidateStoredGraph(committed);
        var manifests = new List<DiscoveredExtension>();
        foreach (var path in Directory.EnumerateFiles(directory, "module.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(directory, path);
            if (relativePath.Split(Path.DirectorySeparatorChar)[0].StartsWith('.')) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Manifest is empty.");
                var error = Validate(manifest, Path.GetDirectoryName(path)!);
                manifests.Add(new(manifest, path, error is null, error, allowlist.Contains(manifest.Id), null)
                { PublisherStatus = GetPublisherStatus(manifest) });
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                var id = "invalid:" + Path.GetRelativePath(directory, path);
                manifests.Add(new(new ModuleManifest { Id = id, Name = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Invalid extension", Version = "0.0.0", Implements = [], MinimumHostVersion = "0.0.0", Runtime = "unknown" }, path, false, exception.Message, false, null));
            }
        }
        lock (syncRoot)
        {
            discovered.Clear();
            foreach (var group in manifests.GroupBy(extension => extension.Manifest.Id, StringComparer.Ordinal))
            {
                var versionGroups = group.GroupBy(extension => extension.Manifest.PackageVersion ?? extension.Manifest.Version, StringComparer.Ordinal)
                    .OrderByDescending(items => NuGet.Versioning.NuGetVersion.TryParse(items.Key, out var version) ? version : new NuGet.Versioning.NuGetVersion(0, 0, 0), NuGet.Versioning.VersionComparer.VersionRelease).ToArray();
                var selected = committed?.Packages.FirstOrDefault(entry => entry.Manifest.Id == group.Key);
                var preferredVersion = selected is null ? versionGroups.FirstOrDefault()?.Key : selected.Manifest.PackageVersion ?? selected.Manifest.Version;
                foreach (var versionGroup in versionGroups)
                {
                    var packages = versionGroup.OrderBy(extension => extension.ManifestPath, StringComparer.Ordinal).ToArray();
                    var key = string.Equals(versionGroup.Key, preferredVersion, StringComparison.Ordinal) ? group.Key : $"{group.Key}@{versionGroup.Key}";
                    if (packages.Length > 1)
                        discovered[key] = packages[0] with { Compatible = false, RejectionReason = "Another package with this id/version is installed." };
                    else discovered[key] = packages[0];
                }
            }
            return Snapshot();
        }
    }

    public IReadOnlyCollection<DiscoveredExtension> List()
    {
        lock (syncRoot) return Snapshot();
    }

    public IReadOnlyCollection<string> ListTrustedPublisherIds()
    {
        lock (publisherTrustGate) return trustedPublisherKeys.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyCollection<string> ListRevokedPublisherIds()
    {
        lock (publisherTrustGate) return revokedPublisherIds.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Checks current package authorization for a loaded extension; statically composed host modules are host-authorized.</summary>
    public bool IsModuleAuthorized(string moduleId, Version moduleVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(moduleVersion);
        _ = Discover();
        lock (syncRoot)
        {
            if (!discovered.TryGetValue(moduleId, out var extension)) return true;
            if (!allowlist.Contains(moduleId) || !extension.Compatible || !Version.TryParse(extension.Manifest.Version, out var declared) || declared != moduleVersion)
                return false;
            try { ValidatePublisherTrust(extension.Manifest); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void AuthorizePublisher(string publisherId, string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        var canonicalPublicKey = rsa.ExportSubjectPublicKeyInfoPem();
        lock (publisherTrustGate)
        {
            trustedPublisherKeys[publisherId] = canonicalPublicKey;
            revokedPublisherIds.Remove(publisherId);
        }
    }

    public async Task RevokePublisherAsync(string publisherId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        lock (publisherTrustGate) revokedPublisherIds.Add(publisherId);
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] activeIds;
            lock (syncRoot) activeIds = loaded.Where(pair => pair.Value.Module is not null && pair.Value.Manifest.PublisherId == publisherId).Select(pair => pair.Key).ToArray();
            var impacted = activeIds.ToHashSet(StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var pair in loaded)
                    if (PackageDependencyResolver.Dependencies(pair.Value.Manifest).Any(dependency => impacted.Contains(dependency.PackageId))) changed |= impacted.Add(pair.Key);
            } while (changed);
            var ordered = loaded.Values.Select(extension => new PackageCandidate(extension.Manifest with
            {
                Dependencies = extension.Manifest.Dependencies.Where(loaded.ContainsKey).ToArray(),
                PackageDependencies = extension.Manifest.PackageDependencies?.Where(dependency => loaded.ContainsKey(dependency.PackageId)).ToArray()
            }, "loaded", extension.PackageDirectory)).ToArray();
            foreach (var id in PackageDependencyResolver.TopologicalOrder(ordered).Reverse().Where(impacted.Contains))
                await DeactivateCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { activationGate.Release(); }
    }

    public async Task<DiscoveredExtension> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            RejectManagedMutation(id);
            var extension = GetDiscovered(id);
            if (extension.Manifest.PackageFormatVersion == 2) throw new InvalidOperationException("Format 2 activation requires a committed package graph.");
            if (!extension.Compatible) throw new InvalidOperationException(extension.RejectionReason ?? "Extension is incompatible.");
            if (!allowlist.Contains(id)) throw new InvalidOperationException("Extension is not present in the host allowlist.");
            ValidatePublisherTrust(extension.Manifest);
            if (loaded.TryGetValue(id, out var existing) && existing.Module is not null)
            {
                // Re-authorization follows the exact loaded generation. Discovery may have selected
                // a newer installed version since this instance was activated.
                ValidatePublisherTrust(existing.Manifest);
                if (existing.Module is OutOfProcessModule external && !await external.IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (runtime.GetHealth(id).State == ModuleLifecycleState.Running)
                        await runtime.MarkDegradedAsync(id, "Out-of-process module has exited.", cancellationToken).ConfigureAwait(false);
                    if (runtime.GetHealth(id).State == ModuleLifecycleState.Degraded)
                        await runtime.StopModuleAsync(id, cancellationToken).ConfigureAwait(false);
                }
                await runtime.StartModuleAsync(id, cancellationToken).ConfigureAwait(false);
                existing.ResumeAccepting();
                jobContributionRegistry?.ResumeModuleBatch(id, existing.Generation);
                RegisterJobContributions(existing);
            }
            else if (trustedFactories.TryGetValue(id, out var factory))
            {
                await runtime.RegisterAndStartAsync(factory(), cancellationToken).ConfigureAwait(false);
            }
            else await LoadAndStartAsync(extension, cancellationToken).ConfigureAwait(false);
            var active = extension with { State = runtime.GetHealth(id).State, Installed = extension.Manifest.EntryAssembly is not null, Loaded = true };
            lock (syncRoot) discovered[id] = active;
            return active;
        }
        finally { activationGate.Release(); }
    }

    public async Task<DiscoveredExtension> UpdateAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LoadedExtension? candidate = null;
        DiscoveredExtension? rollbackTarget = null;
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            RejectManagedMutation(id);
            var installedVersions = Discover();
            if (!allowlist.Contains(id)) throw new InvalidOperationException("Extension is not present in the host allowlist.");
            if (!Version.TryParse(version, out var targetVersion)) throw new ArgumentException("Invalid target extension version.", nameof(version));
            var target = installedVersions.FirstOrDefault(item => item.Manifest.Id == id && Version.TryParse(item.Manifest.Version, out var current) && current == targetVersion)
                ?? throw new ModuleNotFoundException($"{id}@{version}");
            if (target.Manifest.PackageFormatVersion == 2) throw new InvalidOperationException("Format 2 update requires a package-set plan.");
            if (!target.Compatible) throw new InvalidOperationException(target.RejectionReason ?? "Target extension version is incompatible.");
            ValidatePublisherTrust(target.Manifest);
            LoadedExtension? previousLoaded;
            lock (syncRoot) loaded.TryGetValue(id, out previousLoaded);
            rollbackTarget = previousLoaded is not null
                ? installedVersions.FirstOrDefault(item => item.Manifest.Id == id && Version.Parse(item.Manifest.Version) == previousLoaded.Module!.Descriptor.Version)
                : GetDiscovered(id);
            rollbackTarget ??= GetDiscovered(id);
            var previous = rollbackTarget;
            if (Version.Parse(previous.Manifest.Version) == targetVersion) return previous;
            if (target.Manifest.EntryAssembly is null) throw new InvalidOperationException("Updates require an installed, packaged extension version.");

            // Load, validate identity/contracts, start, and health-check the candidate before taking the old generation offline.
            candidate = CreateLoadedModule(target);
            ValidateJobContributions(candidate);
            candidate.StartAttempted = true;
            await candidate.Module!.StartAsync(cancellationToken).ConfigureAwait(false);
            if (candidate.Module is IModuleHealthCheck healthCheck && !await healthCheck.IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Candidate extension failed its activation health check.");
            candidate.Running = true;

            if (previousLoaded is not null)
            {
                previousLoaded.StopAccepting();
                jobContributionRegistry?.BeginModuleDrain(id, previousLoaded.Generation);
                await previousLoaded.DrainAsync(cancellationToken).ConfigureAwait(false);
            }
            if (registry.List().Any(module => module.Descriptor.Id == id))
            {
                await runtime.StopModuleAsync(id, cancellationToken).ConfigureAwait(false);
                if (previousLoaded is not null) jobContributionRegistry?.RemoveModuleBatch(id, previousLoaded.Generation);
                runtime.UnregisterStoppedModule(id);
            }
            if (previousLoaded is not null)
            {
                lock (syncRoot) loaded.Remove(id);
                var oldContext = previousLoaded.Unload();
                previousLoaded = null;
                if (!await WaitForUnloadAsync(oldContext, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Previous extension load context did not unload after its operations drained.");
            }
            runtime.RegisterStartedModule(candidate.Module!);
            lock (syncRoot)
            {
                loaded[id] = candidate;
                discovered[id] = target with { State = ModuleLifecycleState.Running, Installed = true, Loaded = true };
            }
            candidate.ResumeAccepting();
            RegisterJobContributions(candidate);
            candidate = null;
            return target with { State = ModuleLifecycleState.Running, Installed = true, Loaded = true };
        }
        catch (Exception updateFailure)
        {
            if (candidate is not null)
            {
                lock (syncRoot) loaded.Remove(id);
                jobContributionRegistry?.RemoveModuleBatch(id, candidate.Generation);
                if (registry.List().Any(module => module.Descriptor.Id == id))
                {
                    try { await runtime.StopModuleAsync(id, CancellationToken.None).ConfigureAwait(false); } catch { }
                    try { runtime.UnregisterStoppedModule(id); } catch { }
                }
                try { if (candidate.StartAttempted) await candidate.Module!.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                var weak = candidate.Unload();
                candidate = null;
                _ = await WaitForUnloadAsync(weak, CancellationToken.None).ConfigureAwait(false);
            }
            // If the old module had to be removed, restore it from its retained version directory.
            var registered = registry.List().Any(module => module.Descriptor.Id == id);
            if (registered && runtime.GetHealth(id).State is ModuleLifecycleState.Stopped or ModuleLifecycleState.Failed)
            {
                runtime.UnregisterStoppedModule(id);
                registered = false;
            }
            if (registered && runtime.GetHealth(id).State == ModuleLifecycleState.Running && loaded.TryGetValue(id, out var oldGeneration))
            {
                oldGeneration.ResumeAccepting();
                jobContributionRegistry?.ResumeModuleBatch(id, oldGeneration.Generation);
            }
            if (!registered)
            {
                try
                {
                    var previous = rollbackTarget ?? GetDiscovered(id);
                    if (previous.Manifest.EntryAssembly is not null) await LoadAndStartAsync(previous, CancellationToken.None).ConfigureAwait(false);
                    else if (trustedFactories.TryGetValue(id, out var factory)) await runtime.RegisterAndStartAsync(factory(), CancellationToken.None).ConfigureAwait(false);
                    lock (syncRoot) discovered[id] = previous with { State = runtime.GetHealth(id).State };
                }
                catch (Exception rollbackFailure) { throw new AggregateException("Extension update failed and rollback also failed.", updateFailure, rollbackFailure); }
            }
            throw;
        }
        finally { activationGate.Release(); }
    }

    private async Task LoadAndStartAsync(DiscoveredExtension extension, CancellationToken cancellationToken)
    {
        if (registry.List().Any(module => module.Descriptor.Id == extension.Manifest.Id))
            throw new InvalidOperationException($"A different module with ID '{extension.Manifest.Id}' is already registered.");
        var candidate = CreateLoadedModule(extension);
        try
        {
            ValidateJobContributions(candidate);
            candidate.StartAttempted = true;
            await runtime.RegisterAndStartAsync(candidate.Module!, cancellationToken).ConfigureAwait(false);
            candidate.Running = true;
            candidate.ResumeAccepting();
            lock (syncRoot) loaded[extension.Manifest.Id] = candidate;
            RegisterJobContributions(candidate);
        }
        catch
        {
            lock (syncRoot) loaded.Remove(extension.Manifest.Id);
            jobContributionRegistry?.RemoveModuleBatch(extension.Manifest.Id, candidate.Generation);
            if (registry.List().Any(module => module.Descriptor.Id == extension.Manifest.Id))
            {
                try
                {
                    var state = runtime.GetHealth(extension.Manifest.Id).State;
                    if (state is ModuleLifecycleState.Running or ModuleLifecycleState.Degraded or ModuleLifecycleState.Draining)
                        await runtime.StopModuleAsync(extension.Manifest.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                try { runtime.UnregisterStoppedModule(extension.Manifest.Id); } catch (InvalidOperationException) { }
            }
            else try { if (candidate.StartAttempted) await candidate.Module!.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            candidate.Unload();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private LoadedExtension CreateLoadedModule(DiscoveredExtension extension)
    {
        var manifest = extension.Manifest;
        ValidatePublisherTrust(manifest);
        if (manifest.EntryAssembly is null || manifest.EntryType is null) throw new InvalidOperationException("This manifest has no loadable package entry point.");
        var packageDirectory = Path.GetDirectoryName(extension.ManifestPath)!;
        VerifyPackageIntegrity(manifest, packageDirectory);
        if (manifest.Runtime == "dotnet-outofprocess")
        {
            var external = new OutOfProcessModule(packageDirectory, manifest, moduleHostPath, detail =>
            {
                if (runtime.GetHealth(manifest.Id).State == ModuleLifecycleState.Running)
                    runtime.MarkDegradedAsync(manifest.Id, detail).GetAwaiter().GetResult();
            }, resourceLimits, restartPolicy);
            return new LoadedExtension(external, packageDirectory, manifest, running: false);
        }
        var entryPath = ResolvePackageFile(packageDirectory, manifest.EntryAssembly);
        var context = new ExtensionAssemblyLoadContext(entryPath, manifest.SharedAssemblies ?? []);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entryPath);
            var type = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IModule).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("Extension entry type must be a public IModule with a parameterless constructor.");
            var module = (IModule)Activator.CreateInstance(type)!;
            ValidateDescriptor(manifest, module.Descriptor);
            return new LoadedExtension(module, packageDirectory, manifest, running: false, context);
        }
        catch { context.Unload(); throw; }
    }

    public async Task<DiscoveredExtension> DeactivateAsync(string id, CancellationToken cancellationToken)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { RejectManagedMutation(id); return await DeactivateCoreAsync(id, cancellationToken).ConfigureAwait(false); }
        finally { activationGate.Release(); }
    }

    private async Task<DiscoveredExtension> DeactivateCoreAsync(string id, CancellationToken cancellationToken)
    {
        var extension = GetDiscovered(id);
        if (!allowlist.Contains(id)) throw new InvalidOperationException("Extension is not host-trusted.");
        LoadedExtension? loadedExtension = null;
        if (loaded.TryGetValue(id, out loadedExtension))
        {
            loadedExtension.StopAccepting();
            jobContributionRegistry?.BeginModuleDrain(id, loadedExtension.Generation);
            try { await loadedExtension.DrainAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                if (registry.List().Any(module => module.Descriptor.Id == id) && runtime.GetHealth(id).State == ModuleLifecycleState.Running)
                {
                    loadedExtension.ResumeAccepting();
                    jobContributionRegistry?.ResumeModuleBatch(id, loadedExtension.Generation);
                }
                throw;
            }
        }
        try
        {
            if (registry.List().Any(module => module.Descriptor.Id == id)) await runtime.StopModuleAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (loadedExtension is not null && runtime.GetHealth(id).State == ModuleLifecycleState.Running)
            { loadedExtension.ResumeAccepting(); jobContributionRegistry?.ResumeModuleBatch(id, loadedExtension.Generation); }
            throw;
        }
        if (loadedExtension is not null) jobContributionRegistry?.RemoveModuleBatch(id, loadedExtension.Generation);
        if (loaded.TryGetValue(id, out loadedExtension)) loadedExtension.MarkStopped();
        var inactive = extension with { State = registry.List().Any(module => module.Descriptor.Id == id) ? runtime.GetHealth(id).State : null,
            Installed = extension.Manifest.EntryAssembly is not null, Loaded = registry.List().Any(module => module.Descriptor.Id == id) };
        lock (syncRoot) discovered[id] = inactive;
        return inactive;
    }

    public async Task<bool> UnloadAsync(string id, CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectManagedMutation(id);
            await DeactivateCoreAsync(id, cancellationToken).ConfigureAwait(false);
            if (!loaded.Remove(id, out var extension)) return false;
            runtime.UnregisterStoppedModule(id);
            var weak = extension.Unload();
            extension = null!;
            return await WaitForUnloadAsync(weak, cancellationToken).ConfigureAwait(false);
        }
        finally { activationGate.Release(); }
    }

    public ExtensionLease<TContract> Acquire<TContract>(string id) where TContract : class
    {
        lock (syncRoot)
        {
            if (!loaded.TryGetValue(id, out var extension))
            {
                var staticModule = registry.GetById(id);
                if (runtime.GetHealth(id).State != ModuleLifecycleState.Running || staticModule is not TContract staticContract)
                    throw new InvalidOperationException($"Module '{id}' is not active with contract {typeof(TContract).FullName}.");
                return new ExtensionLease<TContract>(staticContract, static () => { });
            }
            if (extension.Manifest.PackageFormatVersion == 2 || File.Exists(PackageCatalogPath) && ReadPackageState().Packages.Any(entry => entry.Manifest.Id == id))
                ValidatePackageExecutionJournal();
            ValidatePublisherTrust(extension.Manifest);
            if (extension.Manifest.PackageFormatVersion == 2) ValidateActivePackageDependencies(extension.Manifest);
            if (runtime.GetHealth(id).State != ModuleLifecycleState.Running)
                throw new InvalidOperationException($"Module '{id}' is {runtime.GetHealth(id).State} and is not accepting operations.");
            var module = extension.Module ?? registry.GetById(id);
            var contract = module is TContract localContract ? localContract : module is IModuleCapabilityProxyFactory factory
                ? factory.CreateCapabilityProxy(typeof(TContract)) as TContract
                : null;
            if (contract is null) throw new InvalidOperationException($"Extension '{id}' does not expose contract {typeof(TContract).FullName}.");
            if (!extension.Running || !extension.TryAcquire()) throw new InvalidOperationException($"Extension '{id}' is not accepting operations.");
            return new ExtensionLease<TContract>(contract, extension.Release);
        }
    }

    private void ValidateJobContributions(LoadedExtension extension)
    {
        var declarations = extension.Manifest.JobContributions ?? [];
        if (declarations.Length == 0) return;
        if (jobContributionRegistry is null) throw new InvalidOperationException("Module declares Platform Job contributions, but the host has no contribution registry.");
        var contractName = typeof(IPlatformJobContributionV1).FullName;
        foreach (var declaration in declarations)
        {
            if (!PlatformJobHandlerRegistryV1.IsValidJobType(declaration.JobType) ||
                !Version.TryParse(declaration.CapabilityVersion, out var capabilityVersion) || capabilityVersion.Major != 1 ||
                !extension.Manifest.Provides.Contains($"{declaration.CapabilityId}@{capabilityVersion}", StringComparer.Ordinal) ||
                !extension.Manifest.ContractTypes.TryGetValue($"{declaration.CapabilityId}@{capabilityVersion}", out var declaredType) || declaredType != contractName)
                throw new InvalidDataException("Module Job contribution does not match the signed capability contract.");
        }
        jobContributionRegistry.ValidateModuleBatch(extension.Manifest.Id, extension.Generation, declarations.Select(item => item.JobType).ToArray());
        using var lease = AcquireJobContributionCandidate(extension);
    }

    private static ExtensionLease<IPlatformJobContributionV1> AcquireJobContributionCandidate(LoadedExtension extension)
    {
        if (extension.Module is IPlatformJobContributionV1 direct) return new(direct, static () => { });
        if (extension.Module is IModuleCapabilityProxyFactory proxy &&
            extension.Manifest.ContractTypes.Values.Contains(typeof(IPlatformJobContributionV1).FullName, StringComparer.Ordinal))
            return new((IPlatformJobContributionV1)proxy.CreateCapabilityProxy(typeof(IPlatformJobContributionV1)), static () => { });
        throw new InvalidDataException("Module does not implement its declared Platform Job contribution capability.");
    }

    private void RegisterJobContributions(LoadedExtension extension)
    {
        var declarations = extension.Manifest.JobContributions ?? [];
        if (declarations.Length == 0) return;
        jobContributionRegistry!.RegisterModuleBatch(extension.Manifest.Id, extension.Generation,
            declarations.Select(item => item.JobType).ToArray(),
            (jobType, payload, cancellationToken) => ExecuteModuleJobAsync(this, extension.Manifest.Id, extension.Generation, jobType, payload, cancellationToken),
            () => IsJobContributionGenerationActive(extension.Manifest.Id, extension.Generation));
    }

    private static async ValueTask<PlatformJobResultMetadataV1?> ExecuteModuleJobAsync(ModuleExtensionCatalog catalog, string moduleId, long generation, string jobType, string payloadJson, CancellationToken cancellationToken)
    {
        if (!catalog.IsJobContributionGenerationActive(moduleId, generation)) throw new InvalidOperationException("Module Job generation is no longer active.");
        using var lease = catalog.AcquireGeneration<IPlatformJobContributionV1>(moduleId, generation);
        var result = await lease.Value.ExecuteAsync(new(jobType, payloadJson), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private ExtensionLease<TContract> AcquireGeneration<TContract>(string moduleId, long generation) where TContract : class
    {
        lock (syncRoot)
        {
            if (!loaded.TryGetValue(moduleId, out var extension) || extension.Generation != generation)
                throw new InvalidOperationException("The requested module generation is no longer active.");
            return Acquire<TContract>(moduleId);
        }
    }

    private bool IsJobContributionGenerationActive(string moduleId, long generation)
    {
        lock (syncRoot)
            return loaded.TryGetValue(moduleId, out var extension) && extension.Generation == generation && extension.Accepting &&
                extension.Running && runtime.GetHealth(moduleId).State == ModuleLifecycleState.Running && allowlist.Contains(moduleId);
    }

    private string? Validate(ModuleManifest manifest, string manifestDirectory)
    {
        try { PackageDependencyResolver.ValidateManifest(manifest); }
        catch (InvalidDataException exception) { return exception.Message; }
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name)) return "Module id and name are required.";
        if (!Version.TryParse(manifest.Version, out var version) || version.Major < 0) return "Invalid module version.";
        if (!Version.TryParse(manifest.MinimumHostVersion, out var minimum) || hostVersion < minimum) return $"Requires host version {manifest.MinimumHostVersion} or newer.";
        if (manifest.MaximumHostVersion is not null && (!Version.TryParse(manifest.MaximumHostVersion, out var maximum) || hostVersion > maximum)) return $"Requires host version {manifest.MaximumHostVersion} or older.";
        if (manifest.Implements is null || manifest.Provides is null || manifest.Requires is null || manifest.Dependencies is null || manifest.DependencyMinimumVersions is null || manifest.OptionalDependencies is null || manifest.Permissions is null) return "Manifest arrays cannot be null.";
        if (manifest.Runtime is not ("dotnet-inprocess-trusted" or "dotnet-outofprocess")) return $"Unsupported runtime '{manifest.Runtime}'.";
        if (manifest.EntryAssembly is not null)
        {
            if (manifest.EntryType is null) return "EntryType is required when EntryAssembly is declared.";
            try { _ = ResolvePackageFile(manifestDirectory, manifest.EntryAssembly); }
            catch (InvalidDataException exception) { return exception.Message; }
        }
        if (manifest.Runtime == "dotnet-outofprocess")
        {
            if (manifest.EntryAssembly is null || manifest.EntryType is null) return "Out-of-process packages require a declared entry assembly and type.";
            if (manifest.ContractTypes is null || manifest.Provides.Any(capability => !manifest.ContractTypes.ContainsKey(capability))) return "Every out-of-process provided capability requires an explicit public CLR contract type.";
        }
        return ValidateContracts(manifest);
    }

    private void ValidatePublisherTrust(ModuleManifest manifest)
    {
        var status = GetPublisherStatus(manifest);
        if (status == PublisherSignatureStatus.Trusted) return;
        if (status == PublisherSignatureStatus.NoSignature && !requirePublisherSignature && manifest.PackageFormatVersion == 1) return;
        throw new InvalidOperationException(status switch
        {
            PublisherSignatureStatus.NoSignature => "Package publisher signature is required by host policy.",
            PublisherSignatureStatus.UnknownPublisher => $"Publisher '{manifest.PublisherId}' is not trusted by host policy.",
            PublisherSignatureStatus.RevokedPublisher => $"Publisher '{manifest.PublisherId}' has been revoked.",
            _ => "Package publisher signature is invalid."
        });
    }

    private PublisherSignatureStatus GetPublisherStatus(ModuleManifest manifest)
    {
        lock (publisherTrustGate) return ModulePackageSignature.Verify(manifest, trustedPublisherKeys, revokedPublisherIds);
    }

    private string? ValidateContracts(ModuleManifest manifest)
    {
        var modules = registry.List();
        foreach (var dependency in manifest.Dependencies ?? [])
        {
            var module = modules.FirstOrDefault(item => item.Descriptor.Id == dependency);
            var minimumText = manifest.DependencyMinimumVersions?.GetValueOrDefault(dependency);
            var minimum = minimumText is null ? null : Version.TryParse(minimumText, out var parsed) ? parsed : null;
            if (minimumText is not null && minimum is null) return $"Invalid minimum version for dependency '{dependency}'.";
            if (module is null && !Directory.Exists(Path.Combine(directory, dependency))) return $"A required module dependency '{dependency}' is unavailable.";
            if (minimum is null) continue;
            var availableVersion = module?.Descriptor.Version;
            var dependencyRoot = Path.Combine(directory, dependency);
            if (Directory.Exists(dependencyRoot))
                foreach (var versionDirectory in Directory.EnumerateDirectories(dependencyRoot))
                    if (Version.TryParse(Path.GetFileName(versionDirectory), out var installedVersion) && (availableVersion is null || installedVersion > availableVersion)) availableVersion = installedVersion;
            if (availableVersion is null || availableVersion < minimum) return $"Dependency '{dependency}' does not meet minimum version {minimum}.";
        }
        foreach (var requirement in manifest.Requires ?? [])
        {
            var separator = requirement.LastIndexOf('@');
            if (separator <= 0 || !Version.TryParse(requirement[(separator + 1)..], out var requiredVersion)) return $"Invalid required contract '{requirement}'.";
            var contractId = requirement[..separator];
            if (!modules.Any(module => module.Descriptor.Provides.Any(provided => provided.ContractId == contractId && provided.Version == requiredVersion)))
                return $"Required public contract '{requirement}' is unavailable.";
        }
        return null;
    }

    private static void ValidateDescriptor(ModuleManifest manifest, ModuleDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Id, manifest.Id, StringComparison.Ordinal) || descriptor.Version != Version.Parse(manifest.Version) || !string.Equals(descriptor.Name, manifest.Name, StringComparison.Ordinal))
            throw new InvalidDataException("Loaded module identity does not match the package manifest.");
        var declared = (manifest.Provides ?? []).Select(ParseCapability).ToHashSet();
        if (!declared.SetEquals(descriptor.Provides.Select(capability => (capability.ContractId, capability.Version))))
            throw new InvalidDataException("Loaded module contract capabilities do not match the package manifest.");
        var declaredRequirements = (manifest.Requires ?? []).Select(ParseCapability).ToHashSet();
        if (!declaredRequirements.SetEquals(descriptor.Requires.Select(capability => (capability.ContractId, capability.Version))))
            throw new InvalidDataException("Loaded module requirements do not match the package manifest.");
        if (!new HashSet<string>(manifest.Dependencies ?? [], StringComparer.Ordinal).SetEquals(descriptor.Dependencies.Select(dependency => dependency.ModuleId)))
            throw new InvalidDataException("Loaded module dependencies do not match the package manifest.");
        foreach (var dependency in descriptor.Dependencies)
            if (manifest.DependencyMinimumVersions is null || !manifest.DependencyMinimumVersions.TryGetValue(dependency.ModuleId, out var minimum) || Version.Parse(minimum) != dependency.MinimumVersion)
                throw new InvalidDataException($"Loaded module minimum dependency version for '{dependency.ModuleId}' does not match the package manifest.");
    }

    private static (string, Version) ParseCapability(string capability)
    {
        var separator = capability.LastIndexOf('@');
        if (separator <= 0 || !Version.TryParse(capability[(separator + 1)..], out var version)) throw new InvalidDataException($"Invalid contract capability '{capability}'.");
        return (capability[..separator], version);
    }

    private static string ResolvePackageFile(string packageDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe package path '{relativePath}'.");
        var root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(packageDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException($"Package file '{relativePath}' is missing or escapes its version directory.");
        var moduleDirectory = Directory.GetParent(Path.GetFullPath(packageDirectory))?.FullName;
        if (moduleDirectory is not null && ((File.GetAttributes(Path.GetFullPath(packageDirectory)) | File.GetAttributes(moduleDirectory)) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Extension package paths cannot contain reparse points.");
        var cursor = Path.GetFullPath(packageDirectory);
        foreach (var segment in relativePath.Split('/'))
        {
            cursor = Path.Combine(cursor, segment);
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Extension package paths cannot contain reparse points.");
        }
        return full;
    }

    public static void ValidateBackupPackage(ModuleManifest manifest, string packageDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.EntryAssembly is null) return;
        PackageDependencyResolver.ValidateManifest(manifest);
        if (manifest.Files is null || !manifest.Files.ContainsKey(manifest.EntryAssembly))
            throw new InvalidDataException("Installed backup package has an unsupported format or missing entry payload.");
        VerifyPackageIntegrity(manifest, packageDirectory);
    }

    private static void VerifyPackageIntegrity(ModuleManifest manifest, string packageDirectory)
    {
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("Package has no integrity file list.");
        foreach (var (relativePath, expectedHash) in manifest.Files)
        {
            var path = ResolvePackageFile(packageDirectory, relativePath);
            using var content = File.OpenRead(path);
            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedHash)))
                throw new InvalidDataException($"Package integrity check failed for '{relativePath}'.");
        }
    }

    private DiscoveredExtension GetDiscovered(string id)
    {
        lock (syncRoot) return discovered.TryGetValue(id, out var found) ? found : throw new ModuleNotFoundException(id);
    }

    private DiscoveredExtension[] Snapshot() => discovered.Values.OrderBy(x => x.Manifest.Id, StringComparer.Ordinal).Select(extension =>
    {
        var selectedSet = ReadState<PackageSetState>(PackageCatalogPath);
        var registered = registry.List().FirstOrDefault(module => module.Descriptor.Id == extension.Manifest.Id);
        var matchesVersion = registered is not null && Version.TryParse(extension.Manifest.Version, out var version) && registered.Descriptor.Version == version;
        if (loaded.TryGetValue(extension.Manifest.Id, out var generation)) matchesVersion &= SameManifest(generation.Manifest, extension.Manifest);
        var state = matchesVersion ? runtime.GetHealth(extension.Manifest.Id).State : (ModuleLifecycleState?)null;
        var isLoaded = generation?.Module is not null && SameManifest(generation.Manifest, extension.Manifest);
        var installed = extension.Manifest.EntryAssembly is not null && (selectedSet is null || selectedSet.Packages.Any(entry => SameManifest(entry.Manifest, extension.Manifest)));
        return extension with { State = state, Installed = installed, Loaded = isLoaded || matchesVersion };
    }).ToArray();

    private static async Task<bool> WaitForUnloadAsync(WeakReference context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; context.IsAlive && attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return !context.IsAlive;
    }

    public void Dispose() { activationGate.Dispose(); }

    private sealed class LoadedExtension(IModule module, string packageDirectory, ModuleManifest manifest, bool running, ExtensionAssemblyLoadContext? context = null)
    {
        private static long nextGeneration;
        private readonly object gate = new();
        private int leases;
        private TaskCompletionSource? drained;
        private bool running = running;
        public ExtensionAssemblyLoadContext? Context { get; private set; } = context;
        public IModule? Module { get; private set; } = module;
        public string PackageDirectory { get; } = packageDirectory;
        public ModuleManifest Manifest { get; } = manifest;
        public long Generation { get; } = Interlocked.Increment(ref nextGeneration);
        public bool Running { get { lock (gate) return running; } set { lock (gate) running = value; } }
        private bool accepting = running;
        public bool Accepting { get { lock (gate) return accepting; } }
        public bool StartAttempted { get; set; }
        public bool TryAcquire() { lock (gate) { if (!accepting || !running) return false; leases++; return true; } }
        public void StopAccepting() { lock (gate) accepting = false; }
        public void ResumeAccepting() { lock (gate) { running = true; accepting = true; if (leases == 0) drained = null; } }
        public void MarkStopped() { lock (gate) { running = false; accepting = false; } }
        public void Release() { lock (gate) { leases--; if (leases == 0) drained?.TrySetResult(); } }
        public Task DrainAsync(CancellationToken cancellationToken) { lock (gate) { if (leases == 0) return Task.CompletedTask; drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously); return drained.Task.WaitAsync(cancellationToken); } }
        public WeakReference Unload()
        {
            lock (gate)
            {
                if (leases != 0) throw new InvalidOperationException("Cannot unload an extension with active operations.");
                var weak = Context is null ? new WeakReference(new object()) : new WeakReference(Context, trackResurrection: true);
                if (Module is IDisposable disposable) disposable.Dispose();
                Module = null;
                Context?.Unload(); Context = null;
                return weak;
            }
        }
    }
}

public sealed class ExtensionLease<TContract>(TContract Contract, Action release) : IDisposable where TContract : class
{
    private Action? releaseOnce = release;
    private TContract? contract = Contract;
    public TContract Value => contract ?? throw new ObjectDisposedException(nameof(ExtensionLease<>));
    public void Dispose()
    {
        contract = null;
        Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }
}
