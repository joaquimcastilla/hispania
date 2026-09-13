namespace Hispania.Library.Contracts;

public sealed record DiscoveredMediaCandidate
{
    public DiscoveredMediaCandidate(LibraryFileReference file, long? byteLength = null, DateTimeOffset? lastModifiedUtc = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));

        if (byteLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "A discovered file length cannot be negative.");
        }

        ByteLength = byteLength;
        LastModifiedUtc = lastModifiedUtc;
    }

    public LibraryFileReference File { get; }

    public long? ByteLength { get; }

    public DateTimeOffset? LastModifiedUtc { get; }
}
