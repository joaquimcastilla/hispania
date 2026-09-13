namespace Hispania.Media.Domain.Sources;

public sealed record SourceLocator
{
    public SourceLocator(SourceScheme scheme, string opaqueValue)
    {
        Scheme = scheme ?? throw new DomainValidationException("source-locator.scheme.required", "A source scheme is required.");

        if (string.IsNullOrWhiteSpace(opaqueValue))
        {
            throw new DomainValidationException("source-locator.opaque-value.required", "A source locator opaque value is required.");
        }

        if (opaqueValue != opaqueValue.Trim())
        {
            throw new DomainValidationException("source-locator.opaque-value.whitespace", "A source locator opaque value cannot have exterior whitespace.");
        }

        if (opaqueValue.Any(char.IsControl))
        {
            throw new DomainValidationException("source-locator.opaque-value.control-character", "A source locator opaque value cannot contain control characters.");
        }

        if (opaqueValue.Length > 4096)
        {
            throw new DomainValidationException("source-locator.opaque-value.length", "A source locator opaque value cannot exceed 4096 characters.");
        }

        OpaqueValue = opaqueValue;
    }

    public SourceScheme Scheme { get; }

    public string OpaqueValue { get; }
}
