namespace Hispania.Library.Contracts;

public interface IMediaProbe
{
    ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default);
}
