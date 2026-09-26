using Hispania.Playback.Contracts;

namespace Hispania.Transcoding.FFmpeg;

public sealed class FfmpegTranscodingCapabilityHealth : ITranscodingCapabilityHealthV1
{
    private readonly object gate = new();
    private int activeJobs;
    private long completedJobs;
    private long failedJobs;
    private long retryCount;
    private long cancelledJobs;
    private DateTimeOffset? lastSuccessUtc;
    private DateTimeOffset? lastFailureUtc;
    private string? lastFailureCode;

    public TranscodingCapabilityHealthSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var status = lastFailureUtc is null ? lastSuccessUtc is null ? "unknown" : "available" :
                lastSuccessUtc > lastFailureUtc ? "available" : "degraded";
            return new(status, activeJobs, completedJobs, failedJobs, retryCount, cancelledJobs, lastSuccessUtc, lastFailureUtc, lastFailureCode);
        }
    }

    internal void JobStarted() { lock (gate) activeJobs++; }
    internal void JobSucceeded() { lock (gate) { activeJobs--; completedJobs++; lastSuccessUtc = DateTimeOffset.UtcNow; } }
    internal void JobFailed(string code) { lock (gate) { activeJobs--; failedJobs++; lastFailureUtc = DateTimeOffset.UtcNow; lastFailureCode = code; } }
    internal void JobCancelled() { lock (gate) { activeJobs--; cancelledJobs++; } }
    internal void Retried() { lock (gate) retryCount++; }
}
