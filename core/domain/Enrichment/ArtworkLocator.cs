namespace Hispania.Media.Domain.Enrichment;

public sealed record ArtworkLocator
{
    public ArtworkLocator(string scheme, string opaqueValue)
    {
        if (string.IsNullOrWhiteSpace(scheme) || scheme != scheme.Trim())
        {
            throw new DomainValidationException("artwork-locator.scheme.invalid", "An ArtworkLocator scheme must use the declared scheme grammar.");
        }

        var normalizedScheme = scheme.ToLowerInvariant();
        if (normalizedScheme.Length > 64 || normalizedScheme[0] is < 'a' or > 'z' || !normalizedScheme.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '-' or '.'))
        {
            throw new DomainValidationException("artwork-locator.scheme.invalid", "An ArtworkLocator scheme must use the declared scheme grammar.");
        }

        Scheme = normalizedScheme;

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
