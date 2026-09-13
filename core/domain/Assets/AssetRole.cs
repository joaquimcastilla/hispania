namespace Hispania.Media.Domain.Assets;

public sealed record AssetRole
{
    private static readonly Dictionary<string, AssetRole> KnownRoles =
        new Dictionary<string, AssetRole>(StringComparer.Ordinal)
        {
            ["main"] = new AssetRole("main"),
            ["dub"] = new AssetRole("dub"),
            ["commentary"] = new AssetRole("commentary"),
            ["audio-description"] = new AssetRole("audio-description"),
            ["subtitle"] = new AssetRole("subtitle"),
            ["lyrics"] = new AssetRole("lyrics")
        };

    private AssetRole(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static AssetRole Main => KnownRoles["main"];

    public static AssetRole Dub => KnownRoles["dub"];

    public static AssetRole Commentary => KnownRoles["commentary"];

    public static AssetRole AudioDescription => KnownRoles["audio-description"];

    public static AssetRole Subtitle => KnownRoles["subtitle"];

    public static AssetRole Lyrics => KnownRoles["lyrics"];

    public static AssetRole From(string code)
    {
        if (code is not null && KnownRoles.TryGetValue(code, out var role))
        {
            return role;
        }

        throw new DomainValidationException("asset-role.unsupported", "The Asset role is not part of the HISPANIA vocabulary.");
    }
}
