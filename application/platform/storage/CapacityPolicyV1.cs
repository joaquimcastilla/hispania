using Hispania.Platform.Contracts.Maintenance;

namespace Hispania.Platform.Storage.Application;

public static class CapacityPolicyV1
{
    public static CapacityPressureLevelV1 Classify(long? totalBytes, long? freeBytes,
        CapacityThresholdsV1 thresholds)
    {
        thresholds.Validate();
        if (totalBytes is null || freeBytes is null) return CapacityPressureLevelV1.Unknown;
        if (totalBytes <= 0 || freeBytes < 0 || freeBytes > totalBytes)
            throw new InvalidDataException("Capacity snapshot contains impossible values.");
        var fraction = (double)freeBytes.Value / totalBytes.Value;
        if (freeBytes <= thresholds.ReserveBytes || fraction <= thresholds.CriticalFreeFraction)
            return CapacityPressureLevelV1.Critical;
        return fraction <= thresholds.WarningFreeFraction ? CapacityPressureLevelV1.Warning : CapacityPressureLevelV1.Normal;
    }

    public static bool CanReserve(CapacitySnapshotV1 snapshot, long requestedBytes,
        CapacityThresholdsV1 thresholds)
    {
        thresholds.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(requestedBytes);
        if (!snapshot.Complete || snapshot.Pressure == CapacityPressureLevelV1.Unknown ||
            snapshot.TotalBytes is not { } total || total <= 0 ||
            snapshot.FreeBytes is not { } free || free < 0 || free > total) return false;
        try { return checked(requestedBytes + thresholds.ReserveBytes) <= free; }
        catch (OverflowException) { return false; }
    }
}
