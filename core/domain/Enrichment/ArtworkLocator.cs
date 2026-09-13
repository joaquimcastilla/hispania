namespace Hispania.Media.Domain.Enrichment;

public sealed record ArtworkLocator
{
    public ArtworkLocator(string scheme, string opaqueValue)
    {
        Scheme = ExternalIdentifierProvider.Normalize(scheme, "artwork-locator.scheme");

        if (string.IsNullOrWhiteSpace(opaqueValue))
        {
            throw new DomainValidationException("artwork-locator.opaque-value.required", "An ArtworkLocator opaque value is required.");
        }

        if (opaqueValue != opaqueValue.Trim() || opaqueValue.Any(char.IsControl))
        {
            throw new DomainValidationException("artwork-locator.opaque-value.invalid", "An ArtworkLocator opaque value cannot have exterior whitespace or control characters.");
        }

        if (opaqueValue.Length > 4096)
        {
            throw new DomainValidationException("artwork-locator.opaque-value.length", "An ArtworkLocator opaque value cannot exceed 4096 characters.");
        }

        OpaqueValue = opaqueValue;
    }

    public string Scheme { get; }

    public string OpaqueValue { get; }
}
