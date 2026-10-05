using System.Text.Json;
using Hispania.Runtime;

namespace Hispania.Server;

public sealed record PersistedExperimentVariant(string ModuleId, string ModuleVersion, string CapabilityId,
    string ContractVersion, long Generation, int BasisPoints);
public sealed record PersistedPlaybackExperiment(int SchemaVersion, string ExperimentId, long Revision, string Mode,
    string CapabilityId, string ContractVersion, IReadOnlyList<PersistedExperimentVariant> Variants,
    string? ActiveModuleId, string? ShadowModuleId, string FallbackPolicy, int AutomaticDisableAfterErrors,
    IReadOnlyList<string> DisabledVariantKeys, string AssignmentSaltFingerprint, DateTimeOffset SavedAtUtc);

/// <summary>Stores an opt-in experiment snapshot and opaque, expiring assignment hashes.</summary>
public interface IPlaybackExperimentStore : IVariantAssignmentStore
{
    bool IsDurable { get; }
    Task<PersistedPlaybackExperiment?> LoadAsync(CancellationToken cancellationToken = default);
    void Save(PersistedPlaybackExperiment? experiment);
    void MarkVariantDisabled(string variantKey);
}

public sealed class InMemoryPlaybackExperimentStore : IPlaybackExperimentStore
{
    private readonly InMemoryVariantAssignmentStore assignments = new();
    private PersistedPlaybackExperiment? experiment;
    public bool IsDurable => false;
    public Task<PersistedPlaybackExperiment?> LoadAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(experiment); }
    public void Save(PersistedPlaybackExperiment? experiment) => this.experiment = experiment;
    public void MarkVariantDisabled(string variantKey)
    {
        if (experiment is null || experiment.DisabledVariantKeys.Contains(variantKey, StringComparer.Ordinal)) return;
        experiment = experiment with { DisabledVariantKeys = experiment.DisabledVariantKeys.Append(variantKey).ToArray() };
    }
    public bool TryGet(string experimentId, string assignmentKey, out VariantIdentity variant) => assignments.TryGet(experimentId, assignmentKey, out variant);
    public void SaveAssignment(string experimentId, string assignmentKey, VariantIdentity variant, DateTimeOffset expiresAtUtc) => assignments.SaveAssignment(experimentId, assignmentKey, variant, expiresAtUtc);
    public void RemoveVariant(string experimentId, VariantIdentity variant) => assignments.RemoveVariant(experimentId, variant);
}

/// <summary>
/// Single-host durable file store. It writes only salted assignment hashes, exact implementation identities, and expiry times.
/// Use a protected local data directory; concurrent multi-host writers require a database-backed implementation.
/// </summary>
public sealed class JsonPlaybackExperimentStore : IPlaybackExperimentStore
{
    private sealed record PersistedAssignment(string ExperimentId, string AssignmentKey, string ModuleId,
        string CapabilityId, string ContractVersion, long Generation, string? ModuleVersion, DateTimeOffset ExpiresAtUtc);
    private sealed record Document(PersistedPlaybackExperiment? Experiment, List<PersistedAssignment> Assignments);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<(string Experiment, string Key), PersistedAssignment> assignments = [];
    private PersistedPlaybackExperiment? experiment;
    private string? loadFailure;

    public JsonPlaybackExperimentStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        if (!File.Exists(this.path)) return;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(this.path))
                ?? throw new InvalidDataException("Playback experiment state file is empty.");
            if (document.Experiment is { SchemaVersion: not 1 }) throw new InvalidDataException("Unsupported playback experiment state schema.");
            experiment = document.Experiment;
            foreach (var entry in document.Assignments)
            {
                if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow) continue;
                if (!IsPseudonym(entry.AssignmentKey) || !Version.TryParse(entry.ContractVersion, out _) ||
                    entry.ModuleVersion is not null && !Version.TryParse(entry.ModuleVersion, out _))
                    throw new InvalidDataException("Playback experiment assignment data is malformed.");
                assignments[(entry.ExperimentId, entry.AssignmentKey)] = entry;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException)
        { assignments.Clear(); experiment = null; loadFailure = exception.Message; }
    }

    public bool IsDurable => true;
    public Task<PersistedPlaybackExperiment?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (loadFailure is not null) throw new InvalidDataException($"Persistent experiment state could not be read: {loadFailure}");
            return Task.FromResult(experiment);
        }
    }

    public void Save(PersistedPlaybackExperiment? experiment)
    {
        lock (gate)
        {
            EnsureWritable();
            this.experiment = experiment;
            if (experiment is null) assignments.Clear();
            WriteAtomic();
            loadFailure = null;
        }
    }

    public void MarkVariantDisabled(string variantKey)
    {
        lock (gate)
        {
            EnsureWritable();
            if (experiment is null || experiment.DisabledVariantKeys.Contains(variantKey, StringComparer.Ordinal)) return;
            experiment = experiment with { DisabledVariantKeys = experiment.DisabledVariantKeys.Append(variantKey).ToArray(), SavedAtUtc = DateTimeOffset.UtcNow };
            WriteAtomic();
        }
    }

    public bool TryGet(string experimentId, string assignmentKey, out VariantIdentity variant)
    {
        lock (gate)
        {
            if (assignments.TryGetValue((experimentId, assignmentKey), out var stored) && stored.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                variant = ToIdentity(stored);
                return true;
            }
            assignments.Remove((experimentId, assignmentKey));
            variant = null!;
            return false;
        }
    }

    public void SaveAssignment(string experimentId, string assignmentKey, VariantIdentity variant, DateTimeOffset expiresAtUtc)
    {
        if (!IsPseudonym(assignmentKey)) throw new ArgumentException("Assignment keys must be SHA-256 pseudonyms.", nameof(assignmentKey));
        lock (gate)
        {
            EnsureWritable();
            assignments[(experimentId, assignmentKey)] = new PersistedAssignment(experimentId, assignmentKey,
                variant.ModuleId, variant.CapabilityId, variant.ContractVersion.ToString(), variant.Generation,
                variant.ModuleVersion?.ToString(), expiresAtUtc);
            if (experiment?.ExperimentId == experimentId) WriteAtomic();
        }
    }

    public void RemoveVariant(string experimentId, VariantIdentity variant)
    {
        lock (gate)
        {
            EnsureWritable();
            foreach (var key in assignments.Where(pair => pair.Key.Experiment == experimentId && ToIdentity(pair.Value) == variant).Select(pair => pair.Key).ToArray())
                assignments.Remove(key);
            if (experiment?.ExperimentId == experimentId) WriteAtomic();
        }
    }

    private void EnsureWritable()
    {
        if (loadFailure is not null) throw new InvalidDataException("Playback experiment schema is incompatible or corrupt; preserve the state file for explicit recovery. No reset was performed.");
    }
    private void WriteAtomic()
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var document = new Document(experiment, assignments.Values.Where(item => item.ExpiresAtUtc > DateTimeOffset.UtcNow).ToList());
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, document, JsonOptions); stream.Flush(flushToDisk: true); }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static VariantIdentity ToIdentity(PersistedAssignment item) => new(item.ModuleId, item.CapabilityId,
        Version.Parse(item.ContractVersion), item.Generation, item.ModuleVersion is null ? null : Version.Parse(item.ModuleVersion));
    private static bool IsPseudonym(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
