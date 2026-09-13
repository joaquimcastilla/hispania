namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record BitRate
{
    public BitRate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            throw new DomainValidationException("bit-rate.invalid", "A declared bit rate must be positive bits per second.");
        }

        BitsPerSecond = bitsPerSecond;
    }

    public long BitsPerSecond { get; }
}
