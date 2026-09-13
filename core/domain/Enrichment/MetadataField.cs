namespace Hispania.Media.Domain.Enrichment;

public sealed record MetadataField
{
    public MetadataField(string code)
    {
        Code = ExternalIdentifierProvider.Normalize(code, "metadata-field.code");
    }

    public string Code { get; }
}
