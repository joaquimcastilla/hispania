namespace Hispania.Library.Contracts;

public interface IMediaDiscovery
{
    IAsyncEnumerable<DiscoveredMediaCandidate> DiscoverAsync(CancellationToken cancellationToken = default);
}
