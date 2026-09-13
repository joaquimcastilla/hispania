namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record StreamLocator
{
    public StreamLocator(int streamIndex)
    {
        if (streamIndex < 0)
        {
            throw new DomainValidationException("stream-locator.index.invalid", "A stream index cannot be negative.");
        }

        StreamIndex = streamIndex;
    }

    public int StreamIndex { get; }
}
