namespace Hispania.Media.Domain.Enrichment;

public sealed record ExternalIdentifierNamespace
{
    public ExternalIdentifierNamespace(string value)
    {
        Value = ExternalIdentifierProvider.Normalize(value, "external-identifier.namespace");
    }

    public string Value { get; }
}
