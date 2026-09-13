namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record FrameRate
{
    public FrameRate(int numerator, int denominator)
    {
        if (numerator <= 0)
        {
            throw new DomainValidationException("frame-rate.numerator.invalid", "A frame rate numerator must be positive.");
        }

        if (denominator <= 0)
        {
            throw new DomainValidationException("frame-rate.denominator.invalid", "A frame rate denominator must be positive.");
        }

        var divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
