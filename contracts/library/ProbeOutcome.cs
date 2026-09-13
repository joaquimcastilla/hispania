namespace Hispania.Library.Contracts;

public abstract record ProbeOutcome;

public sealed record ProbeSucceeded : ProbeOutcome
{
    public ProbeSucceeded(ProbeResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ProbeResult Result { get; }
}

public sealed record ProbeFailed : ProbeOutcome
{
    public ProbeFailed(ProbeFailure failure)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public ProbeFailure Failure { get; }
}

public sealed record ProbeFailure(ProbeFailureKind Kind, string? Detail = null);

public enum ProbeFailureKind
{
    UnsupportedFormat,
    CorruptMedia,
    TimedOut,
    ProcessFailed,
    InvalidOutput
}
