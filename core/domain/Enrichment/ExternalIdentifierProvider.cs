namespace Hispania.Media.Domain.Enrichment;

public sealed record ExternalIdentifierProvider
{
    public ExternalIdentifierProvider(string value)
    {
        Value = Normalize(value, "external-identifier.provider");
    }

    public string Value { get; }

    internal static string Normalize(string value, string codePrefix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(codePrefix + ".required", "An external identifier code is required.");
        }

        if (value.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException(codePrefix + ".invalid", "An external identifier code cannot contain whitespace.");
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Length > 64 || normalized[0] is < 'a' or > 'z' || !normalized.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new DomainValidationException(codePrefix + ".invalid", "An external identifier code must use lowercase letters, digits, and hyphens, beginning with a letter.");
        }

        return normalized;
    }
}
