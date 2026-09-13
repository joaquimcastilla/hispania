namespace Hispania.Library.FFprobe;

public sealed record FfprobeOptions(string ExecutablePath, TimeSpan Timeout, int MaximumStandardOutputBytes = 1_048_576, int MaximumStandardErrorBytes = 65_536)
{
    public FfprobeOptions(string executablePath) : this(executablePath, TimeSpan.FromSeconds(30)) { }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutablePath);
        if (Timeout <= TimeSpan.Zero || MaximumStandardOutputBytes <= 0 || MaximumStandardErrorBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout and output limits must be positive.");
    }
}
