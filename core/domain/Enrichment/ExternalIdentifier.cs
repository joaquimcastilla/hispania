namespace Hispania.Media.Domain.Enrichment;

public sealed record ExternalIdentifier
{
    public ExternalIdentifier(ExternalIdentifierProvider provider, ExternalIdentifierNamespace identifierNamespace, string value)
    {
        Provider = provider ?? throw new DomainValidationException("external-identifier.provider.required", "An ExternalIdentifier provider is required.");
        Namespace = identifierNamespace ?? throw new DomainValidationException("external-identifier.namespace.required", "An ExternalIdentifier namespace is required.");

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException("external-identifier.value.required", "An ExternalIdentifier value is required.");
        }

        if (value != value.Trim() || value.Any(char.IsControl))
        {
            throw new DomainValidationException("external-identifier.value.invalid", "An ExternalIdentifier value cannot have exterior whitespace or control characters.");
        }

        if (value.Length > 1024)
        {
            throw new DomainValidationException("external-identifier.value.length", "An ExternalIdentifier value cannot exceed 1024 characters.");
        }

        Value = value;
    }

    public ExternalIdentifierProvider Provider { get; }

    public ExternalIdentifierNamespace Namespace { get; }

    public string Value { get; }
}
