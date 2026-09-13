namespace Hispania.Media.Domain.Sources;

public sealed record SourceScheme
{
    public SourceScheme(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainValidationException("source-scheme.invalid", "A source scheme must use the declared scheme grammar.");
        }

        var normalized = value.ToLowerInvariant();
        if (normalized[0] is < 'a' or > 'z' || !normalized.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '-' or '.'))
        {
            throw new DomainValidationException("source-scheme.invalid", "A source scheme must use the declared scheme grammar.");
        }

        Value = normalized;
    }

    public string Value { get; }
}
