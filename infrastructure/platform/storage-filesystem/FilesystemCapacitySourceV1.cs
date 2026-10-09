using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Storage.Application;
using System.Diagnostics.Metrics;

namespace Hispania.Platform.Storage.Filesystem;

/// <summary>Observes a configured root; callers never supply a path to this source.</summary>
public sealed class FilesystemCapacitySourceV1 : ICapacitySourceV1
{
    private static readonly Meter Meter = new("Hispania.Capacity", "1.0");
    private static readonly Histogram<int> Pressure = Meter.CreateHistogram<int>("hispania.capacity.pressure");
    private readonly string root;
    private readonly CapacityThresholdsV1 thresholds;
    private readonly TimeProvider clock;

    public FilesystemCapacitySourceV1(string root, string sourceId, CapacityThresholdsV1 thresholds,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 64 ||
            !sourceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            throw new ArgumentException("Capacity source ID is invalid.", nameof(sourceId));
        thresholds.Validate();
        this.root = Path.GetFullPath(root);
        this.thresholds = thresholds;
        this.clock = clock ?? TimeProvider.System;
        SourceId = new(sourceId);
    }

    public MaintenanceOwnerIdV1 SourceId { get; }

    public ValueTask<CapacitySnapshotV1> ObserveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        try
        {
            if (!Directory.Exists(root))
                return ValueTask.FromResult(Record(new CapacitySnapshotV1(SourceId, now, null, null, null,
                    CapacityPressureLevelV1.Unknown, false, "Configured root is unavailable.")));
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var level = CapacityPolicyV1.Classify(total, free, thresholds);
            // Filesystem traversal is intentionally separate and bounded; free space is volume-wide.
            return ValueTask.FromResult(Record(new CapacitySnapshotV1(SourceId, now, total, free, null,
                level, true, null)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ValueTask.FromResult(Record(new CapacitySnapshotV1(SourceId, now, null, null, null,
                CapacityPressureLevelV1.Unknown, false, "Capacity observation unavailable.")));
        }
    }

    private CapacitySnapshotV1 Record(CapacitySnapshotV1 snapshot)
    {
        Pressure.Record((int)snapshot.Pressure, new KeyValuePair<string, object?>("source", SourceId.Value));
        return snapshot;
    }

}
