namespace Hispania.Runtime;

/// <summary>Bounded process recovery policy, independent of module implementation.</summary>
public sealed record OutOfProcessRestartPolicy
{
    public int MaximumRestarts { get; init; } = 3;
    public TimeSpan RecoveryWindow { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (MaximumRestarts is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(MaximumRestarts));
        if (RecoveryWindow <= TimeSpan.Zero || RecoveryWindow > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(RecoveryWindow));
        if (InitialBackoff < TimeSpan.Zero || MaximumBackoff < InitialBackoff || MaximumBackoff > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(InitialBackoff));
    }

    internal TimeSpan GetBackoff(int restartIndex)
    {
        var multiplier = Math.Pow(2, Math.Clamp(restartIndex, 0, 20));
        return TimeSpan.FromMilliseconds(Math.Min(MaximumBackoff.TotalMilliseconds, InitialBackoff.TotalMilliseconds * multiplier));
    }
}
