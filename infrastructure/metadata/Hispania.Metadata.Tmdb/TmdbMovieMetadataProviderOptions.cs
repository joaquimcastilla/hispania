namespace Hispania.Metadata.Tmdb;

public sealed record TmdbMovieMetadataProviderOptions
{
    public bool Enabled { get; init; }
    public string? ReadAccessToken { get; init; }
    public string Language { get; init; } = "es-ES";
    public string Region { get; init; } = "ES";
    public int TimeoutSeconds { get; init; } = 15;

    public void ValidateForEnabledProvider()
    {
        if (Enabled && string.IsNullOrWhiteSpace(ReadAccessToken)) throw new InvalidOperationException("Metadata:Tmdb:ReadAccessToken is required when TMDB is enabled.");
        if (string.IsNullOrWhiteSpace(Language)) throw new InvalidOperationException("Metadata:Tmdb:Language is required.");
        if (string.IsNullOrWhiteSpace(Region)) throw new InvalidOperationException("Metadata:Tmdb:Region is required.");
        if (TimeoutSeconds <= 0 || TimeoutSeconds > 120) throw new InvalidOperationException("Metadata:Tmdb:TimeoutSeconds must be greater than zero and no more than 120.");
    }
}
