namespace Hispania.Media.Domain.Enrichment;

public sealed record ArtworkRole
{
    public ArtworkRole(string code)
    {
        Code = ExternalIdentifierProvider.Normalize(code, "artwork-role.code");
    }

    public string Code { get; }
}
