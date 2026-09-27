using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace Hispania.Runtime;

/// <summary>Public identity of one immutable implementation generation.</summary>
public sealed record VariantIdentity(string ModuleId, string CapabilityId, Version ContractVersion, long Generation, Version? ModuleVersion = null)
{
    public override string ToString() => $"{ModuleId}[{ModuleVersion?.ToString() ?? "unknown"}]/{CapabilityId}@{ContractVersion}#{Generation}";
}

public enum VariantRoutingMode { Manual, Shadow, TrafficSplit }
public enum VariantFallbackPolicy { Fail, RedistributeToHealthy }
public sealed record VariantWeight(VariantIdentity Variant, int BasisPoints);
public sealed record VariantExperimentSnapshot<T>(string ExperimentId, string CapabilityId, Version ContractVersion,
    VariantRoutingMode Mode, IReadOnlyList<VariantWeight> Weights, VariantIdentity? ManualVariant, VariantIdentity? ShadowVariant,
    VariantFallbackPolicy FallbackPolicy, long Revision, IReadOnlyDictionary<VariantIdentity, T> Implementations)
    where T : class;

/// <summary>Optional persistence boundary for stable cohort assignments. Keys are opaque SHA-256 pseudonyms, never user IDs.</summary>
public interface IVariantAssignmentStore
{
    bool TryGet(string experimentId, string assignmentKey, out VariantIdentity variant);
    void SaveAssignment(string experimentId, string assignmentKey, VariantIdentity variant, DateTimeOffset expiresAtUtc);
    void RemoveVariant(string experimentId, VariantIdentity variant);
}

/// <summary>In-memory assignment store. Deployments needing persistence can supply an owned durable implementation.</summary>
public sealed class InMemoryVariantAssignmentStore : IVariantAssignmentStore
{
    private readonly object gate = new();
    private readonly Dictionary<(string Experiment, string Key), (VariantIdentity Variant, DateTimeOffset ExpiresAtUtc)> assignments = [];
    public bool TryGet(string experimentId, string assignmentKey, out VariantIdentity variant)
    {
        lock (gate)
        {
            if (assignments.TryGetValue((experimentId, assignmentKey), out var stored) && stored.ExpiresAtUtc > DateTimeOffset.UtcNow)
            { variant = stored.Variant; return true; }
            assignments.Remove((experimentId, assignmentKey));
            variant = null!;
            return false;
        }
    }
    public void SaveAssignment(string experimentId, string assignmentKey, VariantIdentity variant, DateTimeOffset expiresAtUtc)
    { lock (gate) assignments[(experimentId, assignmentKey)] = (variant, expiresAtUtc); }
    public void RemoveVariant(string experimentId, VariantIdentity variant)
    { lock (gate) foreach (var key in assignments.Where(pair => pair.Key.Experiment == experimentId && pair.Value.Variant == variant).Select(pair => pair.Key).ToArray()) assignments.Remove(key); }
}

/// <summary>
/// Generic experiment router. Configuration changes are atomic; each Resolve call captures one immutable snapshot,
/// so callers must retain its returned variant for the full operation. Weights use basis points and sum to 10,000.
/// </summary>
public sealed class VariantExperimentRouter<T> where T : class
{
    private static readonly Meter Meter = new("Hispania.Runtime.Experiments", "1.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("hispania.experiments.requests");
    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("hispania.experiments.fallbacks");
    private readonly object gate = new();
    private readonly IVariantAssignmentStore assignments;
    private readonly string pseudonymSalt;
    private readonly Dictionary<VariantIdentity, bool> healthy = [];
    private VariantExperimentSnapshot<T>? snapshot;

    public VariantExperimentRouter(IVariantAssignmentStore? assignments = null, string? pseudonymSalt = null)
    {
        this.assignments = assignments ?? new InMemoryVariantAssignmentStore();
        this.pseudonymSalt = pseudonymSalt ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public VariantExperimentSnapshot<T>? Snapshot { get { lock (gate) return snapshot; } }

    public bool UsesPseudonymSalt(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(pseudonymSalt)),
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }

    public VariantExperimentSnapshot<T> Configure(string experimentId, string capabilityId, Version contractVersion,
        VariantRoutingMode mode, IReadOnlyDictionary<VariantIdentity, T> implementations,
        IReadOnlyList<VariantWeight> weights, VariantIdentity? manualVariant = null,
        VariantFallbackPolicy fallbackPolicy = VariantFallbackPolicy.Fail, VariantIdentity? shadowVariant = null,
        long? restoredRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(contractVersion);
        ArgumentNullException.ThrowIfNull(implementations);
        ArgumentNullException.ThrowIfNull(weights);
        if (implementations.Count == 0) throw new ArgumentException("An experiment needs at least one implementation.", nameof(implementations));
        foreach (var identity in implementations.Keys)
            if (identity.CapabilityId != capabilityId || identity.ContractVersion != contractVersion)
                throw new ArgumentException($"Variant '{identity}' does not implement {capabilityId}@{contractVersion}.", nameof(implementations));
        if (mode == VariantRoutingMode.Manual && (manualVariant is null || !implementations.ContainsKey(manualVariant)))
            throw new ArgumentException("Manual mode requires an available selected variant.", nameof(manualVariant));
        if (mode == VariantRoutingMode.Shadow && (manualVariant is null || shadowVariant is null || manualVariant == shadowVariant || !implementations.ContainsKey(manualVariant) || !implementations.ContainsKey(shadowVariant)))
            throw new ArgumentException("Shadow mode requires distinct, available active and secondary variants.", nameof(shadowVariant));
        if (mode != VariantRoutingMode.Shadow && shadowVariant is not null)
            throw new ArgumentException("A shadow variant is only valid in shadow mode.", nameof(shadowVariant));
        if (mode == VariantRoutingMode.TrafficSplit && (weights.Count < 2 || weights.Any(w => w.BasisPoints <= 0 || !implementations.ContainsKey(w.Variant)) || weights.Sum(w => (long)w.BasisPoints) != 10_000 || weights.Select(w => w.Variant).Distinct().Count() != weights.Count))
            throw new ArgumentException("Traffic split weights must name distinct available variants and total exactly 10,000 basis points.", nameof(weights));
        if (mode != VariantRoutingMode.TrafficSplit && weights.Count != 0)
            throw new ArgumentException("Weights are only valid in traffic-split mode.", nameof(weights));
        lock (gate)
        {
            if (restoredRevision is <= 0) throw new ArgumentOutOfRangeException(nameof(restoredRevision));
            var revision = restoredRevision ?? ((snapshot?.Revision ?? 0) + 1);
            if (snapshot is { } previous)
                foreach (var retired in previous.Implementations.Keys.Where(identity => !implementations.ContainsKey(identity)))
                    assignments.RemoveVariant(previous.ExperimentId, retired);
            foreach (var variant in implementations.Keys) if (!healthy.ContainsKey(variant)) healthy[variant] = true;
            snapshot = new VariantExperimentSnapshot<T>(experimentId, capabilityId, contractVersion, mode,
                Array.AsReadOnly(weights.ToArray()), manualVariant, shadowVariant, fallbackPolicy, revision,
                new System.Collections.ObjectModel.ReadOnlyDictionary<VariantIdentity, T>(new Dictionary<VariantIdentity, T>(implementations)));
            return snapshot;
        }
    }

    public T Resolve(string userOrSessionKey, out VariantIdentity selected)
    {
        var current = Snapshot ?? throw new InvalidOperationException("No experiment is configured.");
        return Resolve(current, userOrSessionKey, out _, out selected);
    }

    public T Resolve(string userOrSessionKey, out VariantIdentity assigned, out VariantIdentity selected)
    {
        var current = Snapshot ?? throw new InvalidOperationException("No experiment is configured.");
        return Resolve(current, userOrSessionKey, out assigned, out selected);
    }

    public T Resolve(VariantExperimentSnapshot<T> current, string userOrSessionKey, out VariantIdentity assigned, out VariantIdentity selected)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(userOrSessionKey);
        lock (gate)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pseudonymSalt + "\0" + userOrSessionKey)));
            if (current.Mode is VariantRoutingMode.Manual or VariantRoutingMode.Shadow) selected = current.ManualVariant!;
            else if (assignments.TryGet(current.ExperimentId, key, out var previous) && current.Implementations.ContainsKey(previous)) selected = previous;
            else
            {
                selected = Choose(current, key);
                assignments.SaveAssignment(current.ExperimentId, key, selected, DateTimeOffset.UtcNow.AddDays(30));
            }
            assigned = selected;
            if (!healthy.GetValueOrDefault(selected, false))
            {
                if (current.FallbackPolicy == VariantFallbackPolicy.Fail) throw new VariantUnavailableException(selected);
                var fallback = current.Implementations.Keys.Where(v => healthy.GetValueOrDefault(v, false)).OrderBy(v => v.ToString(), StringComparer.Ordinal).FirstOrDefault();
                if (fallback is null) throw new VariantUnavailableException(selected);
                Fallbacks.Add(1, new KeyValuePair<string, object?>("capability", current.CapabilityId), new KeyValuePair<string, object?>("reason", "unhealthy"));
                selected = fallback;
            }
            Requests.Add(1, new KeyValuePair<string, object?>("capability", current.CapabilityId), new KeyValuePair<string, object?>("variant", selected.ToString()));
            return current.Implementations[selected];
        }
    }

    public T ResolveShadow(out VariantIdentity selected)
    {
        var current = Snapshot ?? throw new InvalidOperationException("No experiment is configured.");
        return ResolveShadow(current, out selected);
    }

    public T ResolveShadow(VariantExperimentSnapshot<T> current, out VariantIdentity selected)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (gate)
        {
            if (current.Mode != VariantRoutingMode.Shadow || current.ShadowVariant is null)
                throw new InvalidOperationException("The configured experiment is not in shadow mode.");
            selected = current.ShadowVariant;
            if (!healthy.GetValueOrDefault(selected, false)) throw new VariantUnavailableException(selected);
            return current.Implementations[selected];
        }
    }

    public void SetHealth(VariantIdentity variant, bool isHealthy)
    { lock (gate) { if (!healthy.ContainsKey(variant)) throw new KeyNotFoundException($"Unknown variant '{variant}'."); healthy[variant] = isHealthy; } }

    public void Retire(VariantIdentity variant)
    {
        lock (gate)
        {
            var current = snapshot ?? throw new InvalidOperationException("No experiment is configured.");
            if (!current.Implementations.ContainsKey(variant)) return;
            var remaining = current.Implementations.Where(pair => pair.Key != variant).ToDictionary();
            if (remaining.Count == 0) throw new InvalidOperationException("An experiment cannot retire its final implementation.");
            assignments.RemoveVariant(current.ExperimentId, variant);
            var weights = current.Weights.Where(item => item.Variant != variant).ToArray();
            var mode = current.Mode;
            if (mode == VariantRoutingMode.TrafficSplit)
            {
                if (weights.Length < 2) { mode = VariantRoutingMode.Manual; weights = []; }
                else
                {
                    var total = weights.Sum(x => x.BasisPoints);
                    weights = weights.Select(x => x with { BasisPoints = (int)((long)x.BasisPoints * 10_000 / total) }).ToArray();
                    weights[^1] = weights[^1] with { BasisPoints = 10_000 - weights.Take(weights.Length - 1).Sum(x => x.BasisPoints) };
                }
            }
            var manual = current.ManualVariant is null || current.ManualVariant == variant ? remaining.Keys.First() : current.ManualVariant;
            var shadow = current.ShadowVariant == variant ? null : current.ShadowVariant;
            if (mode == VariantRoutingMode.Shadow && (shadow is null || shadow == manual || remaining.Count < 2)) { mode = VariantRoutingMode.Manual; shadow = null; }
            snapshot = current with { Implementations = new System.Collections.ObjectModel.ReadOnlyDictionary<VariantIdentity, T>(remaining), Weights = Array.AsReadOnly(weights), Mode = mode, ManualVariant = manual, ShadowVariant = shadow, Revision = current.Revision + 1 };
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (snapshot is { } current)
                foreach (var variant in current.Implementations.Keys) assignments.RemoveVariant(current.ExperimentId, variant);
            snapshot = null;
        }
    }

    private static VariantIdentity Choose(VariantExperimentSnapshot<T> current, string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(current.ExperimentId + "\0" + key));
        var bucket = (int)(BitConverter.ToUInt32(digest, 0) % 10_000);
        foreach (var weight in current.Weights.OrderBy(item => item.Variant.ToString(), StringComparer.Ordinal))
        { if (bucket < weight.BasisPoints) return weight.Variant; bucket -= weight.BasisPoints; }
        throw new InvalidOperationException("Experiment weights are invalid.");
    }
}

public sealed class VariantUnavailableException(VariantIdentity variant) : InvalidOperationException($"Variant '{variant}' is unavailable.");
