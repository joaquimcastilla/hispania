namespace Hispania.Metadata.Tmdb;

public sealed record TmdbMovieMetadataProviderOptions
{
    public bool Enabled { get; init; }
    public string? ReadAccessToken { get; init; }
    public string Language { get; init; } = "es-ES";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public void ValidateForEnabledProvider()
    {
        if (Enabled && string.IsNullOrWhiteSpace(ReadAccessToken)) throw new InvalidOperationException("Metadata:Tmdb:ReadAccessToken is required when TMDB is enabled.");
        if (string.IsNullOrWhiteSpace(Language)) throw new InvalidOperationException("Metadata:Tmdb:Language is required.");
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2)) throw new InvalidOperationException("Metadata:Tmdb:Timeout must be greater than zero and no more than two minutes.");
    }
}
