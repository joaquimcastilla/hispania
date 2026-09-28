using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hispania.Media.Domain.Identity;
using Hispania.ModuleSystem.Contracts;
using Hispania.Playback.Contracts;
using Hispania.Runtime;
using Microsoft.Extensions.Hosting;

namespace Hispania.Server;

public sealed record PlaybackExperimentVariantRequest(string ModuleId, int BasisPoints = 0);
public sealed record PlaybackExperimentConfigurationRequest(
    string ExperimentId,
    string Mode,
    IReadOnlyList<PlaybackExperimentVariantRequest> Variants,
    string? ActiveModuleId = null,
    string? ShadowModuleId = null,
    string FallbackPolicy = "RedistributeToHealthy",
    int AutomaticDisableAfterErrors = 3,
    bool Persistent = false);
public sealed record PlaybackExperimentAuditEntry(DateTimeOffset AtUtc, string Actor, string Action, long Revision, string Detail);
public sealed record PlaybackExperimentVariantMetrics(VariantIdentity Variant, long Assigned, long Executed, long Completed,
    long Errors, long Timeouts, long Cancellations, long Fallbacks, long ShadowErrors, long ShadowCancellations,
    long ShadowDifferences, double MeanLatencyMilliseconds,
    string? LastCorrelationId, string Health);
public sealed record PlaybackExperimentDifference(DateTimeOffset AtUtc, string CorrelationId, VariantIdentity ActiveVariant,
    VariantIdentity ShadowVariant, string ActiveResultType, string ShadowResultType);
public sealed record PlaybackExperimentExport(string ExperimentId, long Revision, DateTimeOffset ExportedAtUtc,
    IReadOnlyCollection<PlaybackExperimentVariantMetrics> Variants, IReadOnlyCollection<PlaybackExperimentAuditEntry> Audit,
    IReadOnlyCollection<PlaybackExperimentDifference> ShadowDifferences);
public sealed record PlaybackExperimentStatus(string ExperimentId, long Revision, string Mode, string CapabilityId,
    string ContractVersion, IReadOnlyCollection<VariantWeight> Weights, VariantIdentity? ActiveVariant,
    VariantIdentity? ShadowVariant, string FallbackPolicy, bool Persistent = false);
public sealed record PlaybackExperimentRecoveryStatus(string State, string? Message);

/// <summary>Playback Planner integration over the generic runtime router and module supervisor.</summary>
public sealed class PlaybackExperimentService : IHostedService, IDisposable
{
    public const string CapabilityId = "hispania.playback.planner";
    public static readonly Version CapabilityVersion = new(1, 0);
    private readonly IModuleRegistry registry;
    private readonly ModuleRuntime runtime;
    private readonly VariantExperimentRouter<IPlaybackPlanner> router;
    private readonly IPlaybackExperimentStore store;
    private readonly ModuleExtensionCatalog? extensionCatalog;
    private readonly string? assignmentSalt;
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly SemaphoreSlim shadowConcurrency = new(2, 2);
    private readonly object routeGate = new();
    private readonly ConcurrentDictionary<VariantIdentity, MutableMetrics> metrics = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<VariantIdentity, byte>> experimentVariants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<VariantIdentity, int> consecutiveErrors = new();
    private readonly ConcurrentDictionary<string, byte> disabledVariantKeys = new(StringComparer.Ordinal);
    private readonly Queue<PlaybackExperimentAuditEntry> audit = new();
    private readonly Queue<PlaybackExperimentDifference> differences = new();
    private readonly ConcurrentDictionary<string, PlaybackExperimentExport> completedExports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> generations = new(StringComparer.Ordinal);
    private readonly Stack<VariantExperimentSnapshot<IPlaybackPlanner>> priorConfigurations = new();
    private bool started;
    private bool persistent;
    private string? recoveryMessage;
    private int automaticDisableAfterErrors = 3;
    private int activePlanCalls;
    private TaskCompletionSource planCallsDrained = CompletedSource();

    public PlaybackExperimentService(IModuleRegistry registry, ModuleRuntime runtime, VariantExperimentRouter<IPlaybackPlanner> router,
        IPlaybackExperimentStore? store = null, string? assignmentSalt = null, ModuleExtensionCatalog? extensionCatalog = null)
    { this.registry = registry; this.runtime = runtime; this.router = router; this.store = store ?? new InMemoryPlaybackExperimentStore(); this.assignmentSalt = assignmentSalt; this.extensionCatalog = extensionCatalog; }

    public VariantExperimentSnapshot<IPlaybackPlanner>? Snapshot => router.Snapshot;

    public PlaybackExperimentStatus? Describe()
    {
        var current = router.Snapshot;
        return current is null ? null : new PlaybackExperimentStatus(current.ExperimentId, current.Revision, current.Mode.ToString(),
            current.CapabilityId, current.ContractVersion.ToString(), current.Weights, current.ManualVariant,
            current.ShadowVariant, current.FallbackPolicy.ToString(), persistent);
    }

    public PlaybackExperimentRecoveryStatus RecoveryStatus => recoveryMessage is null
        ? new PlaybackExperimentRecoveryStatus("Ready", null)
        : recoveryMessage.StartsWith("One or more missing variants", StringComparison.Ordinal)
            ? new PlaybackExperimentRecoveryStatus("RecoveredWithFallback", recoveryMessage)
            : new PlaybackExperimentRecoveryStatus("NotResumed", recoveryMessage);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started) return;
        runtime.StateChanged += OnModuleStateChanged;
        started = true;
        try
        {
            var saved = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (saved is not null) await RestoreAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException or FormatException or IOException or JsonException or ArgumentException or OverflowException)
        { recoveryMessage = $"Persistent experiment was not resumed: {exception.Message}"; }
    }

    private async Task RestoreAsync(PersistedPlaybackExperiment saved, CancellationToken cancellationToken)
    {
        if (saved.SchemaVersion != 1 || saved.Variants.Count is < 1 or > 8)
            throw new InvalidDataException("Persistent experiment has an unsupported schema or participant count.");
        if (saved.AutomaticDisableAfterErrors is < 1 or > 100)
            throw new InvalidDataException("Persistent experiment has an invalid automatic-disable threshold.");
        if (saved.CapabilityId != CapabilityId || Version.Parse(saved.ContractVersion) != CapabilityVersion)
            throw new InvalidDataException("Persistent experiment targets a different capability contract.");
        if (string.IsNullOrWhiteSpace(assignmentSalt) || !router.UsesPseudonymSalt(assignmentSalt) || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(saved.AssignmentSaltFingerprint), SHA256.HashData(Encoding.UTF8.GetBytes(assignmentSalt))))
            throw new InvalidOperationException("Persistent experiment was not resumed because the stable assignment salt is missing or has changed.");
        if (!Enum.TryParse<VariantRoutingMode>(saved.Mode, true, out var mode) ||
            !Enum.TryParse<VariantFallbackPolicy>(saved.FallbackPolicy, true, out var fallback))
            throw new InvalidDataException("Persistent experiment contains an unknown routing or fallback policy.");

        var implementations = new Dictionary<VariantIdentity, IPlaybackPlanner>();
        var byModule = new Dictionary<string, VariantIdentity>(StringComparer.Ordinal);
        var missingVariants = new List<VariantIdentity>();
        foreach (var persisted in saved.Variants)
        {
            var persistedModuleVersion = Version.Parse(persisted.ModuleVersion);
            if (extensionCatalog is not null && !extensionCatalog.IsModuleAuthorized(persisted.ModuleId, persistedModuleVersion))
                throw new InvalidOperationException($"Persistent variant '{persisted.ModuleId}' is no longer authorized by host policy.");
            IModule module;
            try { module = registry.GetById(persisted.ModuleId); }
            catch (ModuleNotFoundException) when (fallback == VariantFallbackPolicy.RedistributeToHealthy)
            {
                missingVariants.Add(new VariantIdentity(persisted.ModuleId, persisted.CapabilityId,
                    Version.Parse(persisted.ContractVersion), persisted.Generation, persistedModuleVersion));
                continue;
            }
            var moduleVersion = Version.Parse(persisted.ModuleVersion);
            var contractVersion = Version.Parse(persisted.ContractVersion);
            if (module.Descriptor.Version != moduleVersion || module is not IPlaybackPlanner planner || module is not IModuleHealthCheck || module is not IModuleExperimentDeclaration)
                throw new InvalidOperationException($"Persistent variant '{persisted.ModuleId}' is missing, changed version, or lacks planner health/declaration contracts.");
            if (persisted.CapabilityId != CapabilityId || contractVersion != CapabilityVersion ||
                module.Descriptor.Provides.SingleOrDefault(item => item.ContractId == CapabilityId && item.Version == CapabilityVersion)?.DynamicallyReplaceable != true)
                throw new InvalidOperationException($"Persistent variant '{persisted.ModuleId}' is no longer a dynamically replaceable compatible provider.");
            var moduleExperiment = (IModuleExperimentDeclaration)module;
            var requiredMode = mode switch
            {
                VariantRoutingMode.Manual => ModuleExperimentMode.Manual,
                VariantRoutingMode.Shadow => ModuleExperimentMode.Shadow,
                _ => ModuleExperimentMode.TrafficSplit
            };
            var declaration = moduleExperiment.ExperimentCapabilities.SingleOrDefault(item => item.ContractId == CapabilityId && item.ContractVersion == CapabilityVersion);
            if (declaration is null || !declaration.Modes.Contains(requiredMode) ||
                mode == VariantRoutingMode.Shadow && declaration.ShadowReadOnlyBoundary != "Hispania.Library.Contracts.IMediaCatalogReader@1.0")
                throw new InvalidOperationException($"Persistent variant '{persisted.ModuleId}' no longer declares the required experiment mode or shadow boundary.");
            var health = runtime.GetHealth(persisted.ModuleId);
            if (health.State != ModuleLifecycleState.Running || !await runtime.CheckHealthAsync(persisted.ModuleId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Persistent variant '{persisted.ModuleId}' is not running and healthy; experiment was not resumed.");
            var identity = new VariantIdentity(persisted.ModuleId, persisted.CapabilityId, contractVersion, persisted.Generation, moduleVersion);
            implementations.Add(identity, planner);
            byModule.Add(persisted.ModuleId, identity);
        }
        if (implementations.Count == 0) throw new InvalidOperationException("No persistent experiment variants remain installed and eligible.");
        var active = saved.ActiveModuleId is null ? null : byModule.GetValueOrDefault(saved.ActiveModuleId);
        var shadow = saved.ShadowModuleId is null ? null : byModule.GetValueOrDefault(saved.ShadowModuleId);
        var recoveredMissingVariant = missingVariants.Count > 0;
        if (recoveredMissingVariant && mode == VariantRoutingMode.Manual && active is null)
            active = implementations.Keys.OrderBy(item => item.ModuleId, StringComparer.Ordinal).FirstOrDefault();
        if (recoveredMissingVariant && mode == VariantRoutingMode.Shadow && shadow is null && active is not null)
        { mode = VariantRoutingMode.Manual; shadow = null; }
        if (recoveredMissingVariant && mode == VariantRoutingMode.Shadow && active is null && shadow is not null)
        { mode = VariantRoutingMode.Manual; active = shadow; shadow = null; }
        if (recoveredMissingVariant && mode == VariantRoutingMode.TrafficSplit && implementations.Count == 1)
        { mode = VariantRoutingMode.Manual; active = implementations.Keys.Single(); }
        if (mode != VariantRoutingMode.TrafficSplit && active is null || mode == VariantRoutingMode.Shadow && shadow is null)
            throw new InvalidDataException("Persistent experiment references a missing active or shadow variant.");
        var weights = mode == VariantRoutingMode.TrafficSplit
            ? NormalizeWeights(saved.Variants.Where(item => byModule.ContainsKey(item.ModuleId))
                .Select(item => new VariantWeight(byModule[item.ModuleId], item.BasisPoints)).ToArray())
            : [];
        // Validate the complete recovered shape before publishing it to the request router.
        var validationRouter = new VariantExperimentRouter<IPlaybackPlanner>(pseudonymSalt: "recovery-shape-validation");
        _ = validationRouter.Configure(saved.ExperimentId, CapabilityId, CapabilityVersion, mode, implementations, weights,
            active, fallback, shadow, saved.Revision);
        foreach (var missing in missingVariants) store.RemoveVariant(saved.ExperimentId, missing);
        var restored = router.Configure(saved.ExperimentId, CapabilityId, CapabilityVersion, mode, implementations, weights,
            active, fallback, shadow, saved.Revision + (recoveredMissingVariant ? 1 : 0));
        automaticDisableAfterErrors = saved.AutomaticDisableAfterErrors;
        persistent = true;
        foreach (var variant in restored.Implementations.Keys)
        {
            generations[variant.ModuleId] = Math.Max(generations.GetValueOrDefault(variant.ModuleId), variant.Generation);
            metrics.TryAdd(variant, new MutableMetrics());
            experimentVariants.GetOrAdd(restored.ExperimentId, _ => new ConcurrentDictionary<VariantIdentity, byte>())[variant] = 0;
            if (saved.DisabledVariantKeys.Contains(VariantKey(variant), StringComparer.Ordinal))
            {
                disabledVariantKeys[VariantKey(variant)] = 0;
                router.SetHealth(variant, false);
            }
        }
        recoveryMessage = recoveredMissingVariant ? "One or more missing variants were removed using RedistributeToHealthy." : null;
        AddAudit(recoveredMissingVariant ? "recovered-with-fallback" : "recovered", restored,
            recoveredMissingVariant ? $"missing variants removed: {string.Join(",", missingVariants.Select(item => item.ModuleId))}" : "validated persistent configuration and compatible live module generations");
        if (recoveredMissingVariant) store.Save(ToPersisted(restored));
    }

    private PersistedPlaybackExperiment ToPersisted(VariantExperimentSnapshot<IPlaybackPlanner> current)
    {
        var weighted = current.Weights.ToDictionary(item => item.Variant, item => item.BasisPoints);
        var variants = current.Implementations.Keys.Select(identity => new PersistedExperimentVariant(identity.ModuleId,
            identity.ModuleVersion?.ToString() ?? throw new InvalidOperationException("Persistent variants require a module version."),
            identity.CapabilityId, identity.ContractVersion.ToString(), identity.Generation, weighted.GetValueOrDefault(identity))).ToArray();
        return new PersistedPlaybackExperiment(1, current.ExperimentId, current.Revision, current.Mode.ToString(),
            current.CapabilityId, current.ContractVersion.ToString(), variants, current.ManualVariant?.ModuleId,
            current.ShadowVariant?.ModuleId, current.FallbackPolicy.ToString(), automaticDisableAfterErrors,
            disabledVariantKeys.Keys.Where(key => current.Implementations.Keys.Any(identity => VariantKey(identity) == key)).ToArray(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assignmentSalt ?? throw new InvalidOperationException("Persistent assignment salt is not configured.")))), DateTimeOffset.UtcNow);
    }

    private static string VariantKey(VariantIdentity identity) => $"{identity.ModuleId}|{identity.ModuleVersion}|{identity.CapabilityId}@{identity.ContractVersion}#{identity.Generation}";

    private static VariantWeight[] NormalizeWeights(VariantWeight[] weights)
    {
        if (weights.Length < 2) throw new InvalidDataException("A traffic-split experiment cannot recover without at least two installed variants.");
        var total = weights.Sum(item => (long)item.BasisPoints);
        if (weights.Any(item => item.BasisPoints <= 0) || total <= 0) throw new InvalidDataException("Persistent experiment weights are invalid.");
        var normalized = weights.Select(item => item with { BasisPoints = Math.Max(1, (int)((long)item.BasisPoints * 10_000 / total)) }).ToArray();
        normalized[^1] = normalized[^1] with { BasisPoints = 10_000 - normalized.Take(normalized.Length - 1).Sum(item => item.BasisPoints) };
        return normalized;
    }

    public async Task<VariantExperimentSnapshot<IPlaybackPlanner>> ConfigureAsync(PlaybackExperimentConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Variants is null || request.Variants.Count is < 2 or > 8) throw new ArgumentException("Playback experiments require between two and eight variants.", nameof(request));
        if (request.AutomaticDisableAfterErrors is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Automatic error threshold must be between 1 and 100.");
        if (!Enum.TryParse<VariantRoutingMode>(request.Mode, true, out var mode)) throw new ArgumentException("Mode must be Manual, Shadow, or TrafficSplit.", nameof(request));
        if (!Enum.TryParse<VariantFallbackPolicy>(request.FallbackPolicy, true, out var fallback)) throw new ArgumentException("FallbackPolicy must be Fail or RedistributeToHealthy.", nameof(request));
        if (request.Variants.Select(item => item.ModuleId).Distinct(StringComparer.Ordinal).Count() != request.Variants.Count)
            throw new ArgumentException("Variant module IDs must be unique.", nameof(request));
        if (request.Persistent && (!store.IsDurable || string.IsNullOrWhiteSpace(assignmentSalt) || assignmentSalt.Length < 32 || !router.UsesPseudonymSalt(assignmentSalt)))
            throw new InvalidOperationException("Persistent experiments require a durable state store and a PlaybackExperiments:AssignmentSalt secret of at least 32 characters.");
        if (mode == VariantRoutingMode.TrafficSplit && (request.Variants.Any(item => item.BasisPoints <= 0) || request.Variants.Sum(item => (long)item.BasisPoints) != 10_000))
            throw new ArgumentException("Traffic split percentages must total 10,000 basis points.", nameof(request));
        if (mode != VariantRoutingMode.TrafficSplit && request.Variants.Any(item => item.BasisPoints != 0))
            throw new ArgumentException("Weights are accepted only for traffic-split mode.", nameof(request));

        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selectedMode = mode switch
            {
                VariantRoutingMode.Manual => ModuleExperimentMode.Manual,
                VariantRoutingMode.Shadow => ModuleExperimentMode.Shadow,
                _ => ModuleExperimentMode.TrafficSplit
            };
            var implementations = new Dictionary<VariantIdentity, IPlaybackPlanner>();
            var moduleIds = new Dictionary<string, VariantIdentity>(StringComparer.Ordinal);
            foreach (var requested in request.Variants)
            {
                var module = registry.GetById(requested.ModuleId);
                var capability = module.Descriptor.Provides.SingleOrDefault(item => item.ContractId == CapabilityId && item.Version == CapabilityVersion);
                if (capability is null || !capability.DynamicallyReplaceable || module is not IPlaybackPlanner planner || module is not IModuleHealthCheck || module is not IModuleExperimentDeclaration declaration)
                    throw new InvalidOperationException($"Module '{requested.ModuleId}' is not a health-checked, declared provider of {CapabilityId}@{CapabilityVersion}.");
                var experimentCapability = declaration.ExperimentCapabilities.SingleOrDefault(item => item.ContractId == CapabilityId && item.ContractVersion == CapabilityVersion);
                if (experimentCapability is null || !experimentCapability.Modes.Contains(selectedMode))
                    throw new InvalidOperationException($"Module '{requested.ModuleId}' does not declare {selectedMode} support for {CapabilityId}@{CapabilityVersion}.");
                if (mode == VariantRoutingMode.Shadow && !string.Equals(experimentCapability.ShadowReadOnlyBoundary, "Hispania.Library.Contracts.IMediaCatalogReader@1.0", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Module '{requested.ModuleId}' lacks the reviewed read-only catalog boundary required by Playback Planner shadow mode.");
                var health = runtime.GetHealth(requested.ModuleId);
                if (health.State != ModuleLifecycleState.Running || !await runtime.CheckHealthAsync(requested.ModuleId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException($"Module '{requested.ModuleId}' must be running and healthy before participating.");
                var existing = router.Snapshot?.Implementations.Keys.FirstOrDefault(item => item.ModuleId == requested.ModuleId && item.ModuleVersion == module.Descriptor.Version);
                var generation = existing?.Generation ?? generations.GetValueOrDefault(requested.ModuleId, 0) + 1;
                generations[requested.ModuleId] = Math.Max(generation, generations.GetValueOrDefault(requested.ModuleId));
                var identity = existing ?? new VariantIdentity(module.Descriptor.Id, CapabilityId, CapabilityVersion, generation, module.Descriptor.Version);
                implementations.Add(identity, planner);
                moduleIds.Add(requested.ModuleId, identity);
            }
            var active = mode == VariantRoutingMode.TrafficSplit ? null : moduleIds.GetValueOrDefault(request.ActiveModuleId ?? (mode == VariantRoutingMode.Manual ? request.Variants[0].ModuleId : null) ?? string.Empty);
            var shadow = mode == VariantRoutingMode.Shadow ? moduleIds.GetValueOrDefault(request.ShadowModuleId ?? string.Empty) : null;
            if (mode != VariantRoutingMode.TrafficSplit && active is null) throw new ArgumentException("ActiveModuleId must select a participating variant.", nameof(request));
            if (mode == VariantRoutingMode.Shadow && (shadow is null || shadow == active)) throw new ArgumentException("Shadow mode requires a distinct ShadowModuleId.", nameof(request));
            var weights = mode == VariantRoutingMode.TrafficSplit
                ? request.Variants.Select(item => new VariantWeight(moduleIds[item.ModuleId], item.BasisPoints)).ToArray()
                : [];
            var isReconfiguration = router.Snapshot is { } previous && previous.ExperimentId == request.ExperimentId;
            if (router.Snapshot is { } previousConfiguration) priorConfigurations.Push(previousConfiguration);
            automaticDisableAfterErrors = request.AutomaticDisableAfterErrors;
            var configured = router.Configure(request.ExperimentId, CapabilityId, CapabilityVersion, mode, implementations,
                weights, active, fallback, shadow);
            foreach (var variant in implementations.Keys)
            {
                router.SetHealth(variant, true);
                disabledVariantKeys.TryRemove(VariantKey(variant), out _);
                metrics.TryAdd(variant, new MutableMetrics());
                experimentVariants.GetOrAdd(configured.ExperimentId, _ => new ConcurrentDictionary<VariantIdentity, byte>())[variant] = 0;
            }
            AddAudit(isReconfiguration ? "configure" : "start", configured,
                string.Join(",", request.Variants.Select(item => $"{item.ModuleId}:{item.BasisPoints}")));
            persistent = request.Persistent;
            if (!persistent) disabledVariantKeys.Clear();
            store.Save(persistent ? ToPersisted(configured) : null);
            return configured;
        }
        finally { configurationGate.Release(); }
    }

    public async Task<VariantExperimentSnapshot<IPlaybackPlanner>> RetireVariantAsync(string experimentId, string moduleId,
        CancellationToken cancellationToken = default)
    {
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireExperiment(experimentId);
            var identity = current.Implementations.Keys.SingleOrDefault(item => item.ModuleId == moduleId);
            if (identity is null) throw new KeyNotFoundException($"Variant '{moduleId}' is not part of experiment '{experimentId}'.");
            router.Retire(identity);
            var retired = router.Snapshot!;
            AddAudit("retire-variant", retired, $"{moduleId}; module remains loaded for captured operations and other capabilities");
            if (persistent) store.Save(ToPersisted(retired));
            return retired;
        }
        finally { configurationGate.Release(); }
    }

    public async Task<VariantExperimentSnapshot<IPlaybackPlanner>> SuspendAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireExperiment(experimentId);
            var selected = current.ManualVariant ?? (current.Weights.Count > 0 ? current.Weights[0].Variant : current.Implementations.Keys.First());
            priorConfigurations.Push(current);
            var suspended = router.Configure(current.ExperimentId, current.CapabilityId, current.ContractVersion, VariantRoutingMode.Manual,
                current.Implementations, [], selected, current.FallbackPolicy);
            AddAudit("suspend", suspended, selected.ModuleId);
            if (persistent) store.Save(ToPersisted(suspended));
            return suspended;
        }
        finally { configurationGate.Release(); }
    }

    public async Task<VariantExperimentSnapshot<IPlaybackPlanner>> SuspendVariantAsync(string experimentId, string moduleId,
        CancellationToken cancellationToken = default)
    {
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireExperiment(experimentId);
            var variant = current.Implementations.Keys.SingleOrDefault(item => item.ModuleId == moduleId);
            if (variant is null) throw new KeyNotFoundException($"Variant '{moduleId}' is not part of experiment '{experimentId}'.");
            priorConfigurations.Push(current);
            router.SetHealth(variant, false);
            disabledVariantKeys[VariantKey(variant)] = 0;
            if (persistent) store.MarkVariantDisabled(VariantKey(variant));
            AddAudit("suspend-variant", current, $"{moduleId}; new assignments follow {current.FallbackPolicy}");
            return current;
        }
        finally { configurationGate.Release(); }
    }

    public async Task<VariantExperimentSnapshot<IPlaybackPlanner>> RollbackAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = RequireExperiment(experimentId);
            if (priorConfigurations.Count == 0) throw new InvalidOperationException("No prior experiment configuration is available for rollback.");
            var previous = priorConfigurations.Peek();
            foreach (var identity in previous.Implementations.Keys)
                if (runtime.GetHealth(identity.ModuleId).State != ModuleLifecycleState.Running || !await runtime.CheckHealthAsync(identity.ModuleId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException($"Cannot roll back to unhealthy or stopped module '{identity.ModuleId}'.");
            var restored = router.Configure(previous.ExperimentId, previous.CapabilityId, previous.ContractVersion, previous.Mode,
                previous.Implementations, previous.Weights, previous.ManualVariant, previous.FallbackPolicy, previous.ShadowVariant);
            foreach (var identity in restored.Implementations.Keys)
            {
                router.SetHealth(identity, true);
                disabledVariantKeys.TryRemove(VariantKey(identity), out _);
            }
            _ = priorConfigurations.Pop();
            AddAudit("rollback", restored, $"restored revision {previous.Revision}");
            if (persistent) store.Save(ToPersisted(restored));
            return restored;
        }
        finally { configurationGate.Release(); }
    }

    public async Task FinishAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireExperiment(experimentId);
            AddAudit("finish", current, "experiment finished; new calls use the configured default planner");
            if (persistent) store.Save(null);
            Task drained;
            lock (routeGate)
            {
                router.Clear();
                drained = activePlanCalls == 0 ? Task.CompletedTask : planCallsDrained.Task;
            }
            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            completedExports[current.ExperimentId] = CreateExport(current);
            persistent = false;
        }
        finally { configurationGate.Release(); }
    }

    public PlaybackPlanningResult Plan(string subjectKey, MediaEntityId mediaEntityId, IPlaybackPlanner defaultPlanner, string correlationId)
    {
        VariantExperimentSnapshot<IPlaybackPlanner>? current;
        lock (routeGate)
        {
            current = router.Snapshot;
            if (current is not null && activePlanCalls++ == 0) planCallsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (current is null) return defaultPlanner.Plan(mediaEntityId);
        try { return PlanWithSnapshot(current, subjectKey, mediaEntityId, correlationId); }
        finally
        {
            lock (routeGate)
            {
                if (--activePlanCalls == 0) planCallsDrained.TrySetResult();
            }
        }
    }

    private PlaybackPlanningResult PlanWithSnapshot(VariantExperimentSnapshot<IPlaybackPlanner> current, string subjectKey,
        MediaEntityId mediaEntityId, string correlationId)
    {
        var active = router.Resolve(current, subjectKey, out var assignedIdentity, out var activeIdentity);
        var assignedMetrics = metrics.GetOrAdd(assignedIdentity, _ => new MutableMetrics());
        Interlocked.Increment(ref assignedMetrics.Assigned);
        var activeMetrics = metrics.GetOrAdd(activeIdentity, _ => new MutableMetrics());
        Interlocked.Increment(ref activeMetrics.Executed);
        if (assignedIdentity != activeIdentity) Interlocked.Increment(ref assignedMetrics.Fallbacks);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = active.Plan(mediaEntityId);
            timer.Stop();
            activeMetrics.RecordCompleted(timer.Elapsed.TotalMilliseconds, correlationId);
            consecutiveErrors.TryRemove(activeIdentity, out _);
            if (current.Mode == VariantRoutingMode.Shadow)
                _ = CompareShadowAsync(current, result, mediaEntityId, activeIdentity, correlationId);
            return result;
        }
        catch (OperationCanceledException)
        {
            timer.Stop(); Interlocked.Increment(ref activeMetrics.Cancellations); throw;
        }
        catch (TimeoutException)
        {
            timer.Stop(); Interlocked.Increment(ref activeMetrics.Errors); Interlocked.Increment(ref activeMetrics.Timeouts);
            var streak = consecutiveErrors.AddOrUpdate(activeIdentity, 1, (_, previous) => previous + 1);
            if (streak >= automaticDisableAfterErrors) SuspendFailedVariant(activeIdentity, "consecutive planner timeouts reached the configured threshold");
            throw;
        }
        catch
        {
            timer.Stop();
            Interlocked.Increment(ref activeMetrics.Errors);
            activeMetrics.RecordLatency(timer.Elapsed.TotalMilliseconds, correlationId);
            var streak = consecutiveErrors.AddOrUpdate(activeIdentity, 1, (_, previous) => previous + 1);
            if (streak >= automaticDisableAfterErrors) SuspendFailedVariant(activeIdentity, "consecutive planner errors reached the configured threshold");
            throw;
        }
    }

    private async Task CompareShadowAsync(VariantExperimentSnapshot<IPlaybackPlanner> current, PlaybackPlanningResult activeResult,
        MediaEntityId mediaEntityId, VariantIdentity activeIdentity, string correlationId)
    {
        var shadowIdentity = current.ShadowVariant;
        if (shadowIdentity is null) return;
        var shadowMetrics = metrics.GetOrAdd(shadowIdentity, _ => new MutableMetrics());
        var shadowSlotOwned = false;
        try
        {
            if (!await shadowConcurrency.WaitAsync(0).ConfigureAwait(false))
            {
                Interlocked.Increment(ref shadowMetrics.Assigned);
                Interlocked.Increment(ref shadowMetrics.ShadowErrors);
                return;
            }
            shadowSlotOwned = true;
            var shadow = router.ResolveShadow(current, out var resolvedIdentity);
            if (resolvedIdentity != shadowIdentity) { shadowConcurrency.Release(); shadowSlotOwned = false; return; }
            Interlocked.Increment(ref shadowMetrics.Assigned);
            Interlocked.Increment(ref shadowMetrics.Executed);
            var timer = Stopwatch.StartNew();
            var shadowTask = Task.Run(() =>
            {
                try { return shadow.Plan(mediaEntityId); }
                finally { shadowConcurrency.Release(); }
            });
            shadowSlotOwned = false;
            PlaybackPlanningResult shadowResult;
            try { shadowResult = await shadowTask.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                timer.Stop();
                Interlocked.Increment(ref shadowMetrics.Timeouts);
                _ = shadowTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
            timer.Stop();
            shadowMetrics.RecordCompleted(timer.Elapsed.TotalMilliseconds, correlationId);
            if (Equals(activeResult, shadowResult)) return;
            Interlocked.Increment(ref shadowMetrics.ShadowDifferences);
            lock (differences)
            {
                differences.Enqueue(new PlaybackExperimentDifference(DateTimeOffset.UtcNow, correlationId, activeIdentity,
                    shadowIdentity, activeResult.GetType().Name, shadowResult.GetType().Name));
                while (differences.Count > 500) differences.Dequeue();
            }
        }
        catch (OperationCanceledException) { Interlocked.Increment(ref shadowMetrics.ShadowCancellations); }
        catch (Exception shadowFailure) when (shadowFailure is not OutOfMemoryException and not StackOverflowException)
        { _ = shadowFailure; Interlocked.Increment(ref shadowMetrics.ShadowErrors); }
        finally { if (shadowSlotOwned) shadowConcurrency.Release(); }
    }

    public PlaybackPlanningResult PlanForCurrentRequest(MediaEntityId mediaEntityId, IPlaybackPlanner defaultPlanner, HttpContext? context)
    {
        if (router.Snapshot is null) return defaultPlanner.Plan(mediaEntityId);
        string subjectKey;
        if (context?.User.Identity?.IsAuthenticated == true)
            subjectKey = "user:" + (context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity.Name ?? "authenticated");
        else if (context is not null && context.Request.Cookies.TryGetValue("hispania-experiment-session", out var existing) && Guid.TryParseExact(existing, "N", out _))
            subjectKey = "session:" + existing;
        else
        {
            subjectKey = "session:" + Guid.NewGuid().ToString("N");
            context?.Response.Cookies.Append("hispania-experiment-session", subjectKey[8..], new CookieOptions
            { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true, Expires = DateTimeOffset.UtcNow.AddDays(30) });
        }
        return Plan(subjectKey, mediaEntityId, defaultPlanner, Activity.Current?.TraceId.ToString() ?? context?.TraceIdentifier ?? Guid.NewGuid().ToString("N"));
    }

    public PlaybackExperimentExport Export(string experimentId)
    {
        var current = router.Snapshot;
        if (current is null || !string.Equals(current.ExperimentId, experimentId, StringComparison.Ordinal))
        {
            if (completedExports.TryGetValue(experimentId, out var completed)) return completed;
            throw new KeyNotFoundException(experimentId);
        }
        return CreateExport(current);
    }

    private PlaybackExperimentExport CreateExport(VariantExperimentSnapshot<IPlaybackPlanner> current)
    {
        var identities = experimentVariants.GetOrAdd(current.ExperimentId, _ => new ConcurrentDictionary<VariantIdentity, byte>())
            .Keys.ToArray();
        var variants = identities.Select(identity =>
        {
            var value = metrics.GetOrAdd(identity, _ => new MutableMetrics());
            var health = disabledVariantKeys.ContainsKey(VariantKey(identity)) ? "Suspended" : runtime.GetHealth(identity.ModuleId).State.ToString();
            return value.Snapshot(identity, health);
        }).ToArray();
        PlaybackExperimentDifference[] comparisons;
        var identitySet = identities.ToHashSet();
        lock (differences) comparisons = differences.Where(item => identitySet.Contains(item.ActiveVariant) || identitySet.Contains(item.ShadowVariant)).ToArray();
        lock (audit) return new PlaybackExperimentExport(current.ExperimentId, current.Revision, DateTimeOffset.UtcNow, variants, audit.ToArray(), comparisons);
    }

    private void OnModuleStateChanged(ModuleHealthTransition transition)
    {
        var current = router.Snapshot;
        if (current is null) return;
        foreach (var identity in current.Implementations.Keys.Where(item => item.ModuleId == transition.ModuleId))
        {
            var healthy = transition.To == ModuleLifecycleState.Running && !disabledVariantKeys.ContainsKey(VariantKey(identity));
            try { router.SetHealth(identity, healthy); } catch (KeyNotFoundException) { }
        }
    }

    private void SuspendFailedVariant(VariantIdentity variant, string reason)
    {
        try
        {
            router.SetHealth(variant, false);
            disabledVariantKeys[VariantKey(variant)] = 0;
            if (persistent) store.MarkVariantDisabled(VariantKey(variant));
            var current = router.Snapshot;
            if (current is not null) AddAudit("automatic-fallback", current, $"{variant}: {reason}");
            if (runtime.GetHealth(variant.ModuleId).State == ModuleLifecycleState.Running)
                _ = runtime.MarkDegradedAsync(variant.ModuleId, reason);
        }
        catch (KeyNotFoundException) { }
    }

    private VariantExperimentSnapshot<IPlaybackPlanner> RequireExperiment(string experimentId)
    {
        var current = router.Snapshot ?? throw new InvalidOperationException("No playback experiment is active.");
        if (!string.Equals(current.ExperimentId, experimentId, StringComparison.Ordinal)) throw new KeyNotFoundException(experimentId);
        return current;
    }

    private void AddAudit(string action, VariantExperimentSnapshot<IPlaybackPlanner> snapshot, string detail)
    {
        lock (audit)
        {
            audit.Enqueue(new PlaybackExperimentAuditEntry(DateTimeOffset.UtcNow, "authenticated-admin", action, snapshot.Revision, detail));
            while (audit.Count > 200) audit.Dequeue();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    { if (started) runtime.StateChanged -= OnModuleStateChanged; started = false; return Task.CompletedTask; }

    public void Dispose() { if (started) runtime.StateChanged -= OnModuleStateChanged; configurationGate.Dispose(); }

    private sealed class MutableMetrics
    {
        private long latencyTicks;
        private long latencyCount;
        private string? lastCorrelationId;
        public long Assigned;
        public long Executed;
        public long Completed;
        public long Errors;
        public long Timeouts;
        public long Cancellations;
        public long Fallbacks;
        public long ShadowErrors;
        public long ShadowCancellations;
        public long ShadowDifferences;
        public void RecordCompleted(double latency, string correlationId)
        { Interlocked.Increment(ref Completed); RecordLatency(latency, correlationId); }
        public void RecordLatency(double latency, string correlationId)
        { Interlocked.Add(ref latencyTicks, (long)(latency * 1_000)); Interlocked.Increment(ref latencyCount); Volatile.Write(ref lastCorrelationId, correlationId); }
        public PlaybackExperimentVariantMetrics Snapshot(VariantIdentity variant, string health)
        {
            var count = Interlocked.Read(ref latencyCount);
            var mean = count == 0 ? 0 : (double)Interlocked.Read(ref latencyTicks) / count / 1_000;
            return new PlaybackExperimentVariantMetrics(variant, Interlocked.Read(ref Assigned), Interlocked.Read(ref Executed),
                Interlocked.Read(ref Completed), Interlocked.Read(ref Errors), Interlocked.Read(ref Timeouts), Interlocked.Read(ref Cancellations),
                Interlocked.Read(ref Fallbacks), Interlocked.Read(ref ShadowErrors), Interlocked.Read(ref ShadowCancellations),
                Interlocked.Read(ref ShadowDifferences), mean, Volatile.Read(ref lastCorrelationId), health);
        }
    }

    private static TaskCompletionSource CompletedSource()
    { var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); completion.SetResult(); return completion; }
}
