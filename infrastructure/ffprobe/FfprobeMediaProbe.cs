using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;

namespace Hispania.Library.FFprobe;

public sealed class FfprobeMediaProbe : IMediaProbe
{
    private readonly LocalLibraryRootConfiguration _roots; private readonly FfprobeOptions _options; private readonly IFfprobeProcessRunner _runner;
    public FfprobeMediaProbe(LocalLibraryRootConfiguration roots, FfprobeOptions options) : this(roots, options, new FfprobeProcessRunner()) { }
    internal FfprobeMediaProbe(LocalLibraryRootConfiguration roots, FfprobeOptions options, IFfprobeProcessRunner runner) { _roots = roots ?? throw new ArgumentNullException(nameof(roots)); _options = options ?? throw new ArgumentNullException(nameof(options)); _options.Validate(); _runner = runner ?? throw new ArgumentNullException(nameof(runner)); }
    public async ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate); cancellationToken.ThrowIfCancellationRequested();
        var root = _roots.Roots.SingleOrDefault(item => string.Equals(item.RootKey, candidate.File.RootKey, StringComparison.Ordinal)) ?? throw new ArgumentException("The library root is not configured.", nameof(candidate));
        var path = RootPathResolver.ResolveCandidatePath(root, candidate.File.RelativePath);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return new ProbeFailed(new ProbeFailure(ProbeFailureKind.ProcessFailed));
        var result = await _runner.RunAsync(_options, path, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut) return new ProbeFailed(new ProbeFailure(ProbeFailureKind.TimedOut));
        if (result.StartFailed || result.OutputLimitExceeded) return new ProbeFailed(new ProbeFailure(ProbeFailureKind.ProcessFailed));
        if (result.ExitCode is not 0) return new ProbeFailed(new ProbeFailure(IsCorrupt(result.StandardError) ? ProbeFailureKind.CorruptMedia : ProbeFailureKind.ProcessFailed));
        return FfprobeJsonParser.Parse(result.StandardOutput);
    }
    private static bool IsCorrupt(string stderr) => stderr.Contains("invalid data", StringComparison.OrdinalIgnoreCase) || stderr.Contains("moov atom", StringComparison.OrdinalIgnoreCase);
}
