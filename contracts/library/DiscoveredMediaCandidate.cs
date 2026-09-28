namespace Hispania.Library.Contracts;

public sealed record DiscoveredMediaCandidate
{
    public DiscoveredMediaCandidate(LibraryFileReference file, long? byteLength = null, DateTimeOffset? lastModifiedUtc = null, Hispania.Media.Domain.Sources.MediaSource? source = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));

        if (byteLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "A discovered file length cannot be negative.");
        }

        ByteLength = byteLength;
        LastModifiedUtc = lastModifiedUtc;
        Source = source;
    }

    public LibraryFileReference File { get; }

    public long? ByteLength { get; }

    public DateTimeOffset? LastModifiedUtc { get; }

    public Hispania.Media.Domain.Sources.MediaSource? Source { get; }

    public Hispania.Media.Domain.Identity.MediaSourceId GetOrCreateSourceId() =>
        Source?.Id ?? Hispania.Media.Domain.Identity.MediaSourceId.New();
}
