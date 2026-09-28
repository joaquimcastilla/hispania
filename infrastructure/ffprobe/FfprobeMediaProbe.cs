using Hispania.Library.Contracts;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.Identity;

namespace Hispania.Library.FFprobe;

public sealed class FfprobeMediaProbe : IMediaProbe
{
    private readonly IMediaSourceContentAccessV1 _sourceAccess; private readonly FfprobeOptions _options; private readonly IFfprobeProcessRunner _runner;
    public FfprobeMediaProbe(IMediaSourceContentAccessV1 sourceAccess, FfprobeOptions options) : this(sourceAccess, options, new FfprobeProcessRunner()) { }
    internal FfprobeMediaProbe(IMediaSourceContentAccessV1 sourceAccess, FfprobeOptions options, IFfprobeProcessRunner runner) { _sourceAccess = sourceAccess ?? throw new ArgumentNullException(nameof(sourceAccess)); _options = options ?? throw new ArgumentNullException(nameof(options)); _options.Validate(); _runner = runner ?? throw new ArgumentNullException(nameof(runner)); }
    public async ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate); cancellationToken.ThrowIfCancellationRequested();
        var source = candidate.Source ?? new MediaSource(MediaSourceId.New(), LibrarySourceLocator.From(candidate.File));
        var opened = await _sourceAccess.OpenMediaSourceReadAsync(source, cancellationToken).ConfigureAwait(false);
        if (opened is not MediaSourceContentOpened { Content: var content }) return new ProbeFailed(new ProbeFailure(ProbeFailureKind.ProcessFailed));
        await using (content.ConfigureAwait(false))
        {
            var result = await _runner.RunAsync(_options, content.Content, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut) return new ProbeFailed(new ProbeFailure(ProbeFailureKind.TimedOut));
            if (result.StartFailed || result.OutputLimitExceeded) return new ProbeFailed(new ProbeFailure(ProbeFailureKind.ProcessFailed));
            if (result.ExitCode is not 0) return new ProbeFailed(new ProbeFailure(IsCorrupt(result.StandardError) ? ProbeFailureKind.CorruptMedia : ProbeFailureKind.ProcessFailed));
            return FfprobeJsonParser.Parse(result.StandardOutput);
        }
    }
    private static bool IsCorrupt(string stderr) => stderr.Contains("invalid data", StringComparison.OrdinalIgnoreCase) || stderr.Contains("moov atom", StringComparison.OrdinalIgnoreCase);
}
