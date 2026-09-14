using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;

namespace Hispania.Metadata.Tmdb;

/// <summary>TMDB movie adapter. TMDB wire types deliberately do not escape this assembly.</summary>
public sealed class TmdbMovieMetadataProvider : IMovieMetadataProvider
{
    public static readonly ExternalIdentifierProvider Provider = new("tmdb");
    public static readonly ExternalIdentifierNamespace MovieNamespace = new("movie");
    private const string MappingVersion = "tmdb-movie-v1";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient httpClient;
    private readonly TmdbMovieMetadataProviderOptions options;
    private readonly Func<DateTimeOffset> utcNow;

    public TmdbMovieMetadataProvider(HttpClient httpClient, TmdbMovieMetadataProviderOptions options, Func<DateTimeOffset>? utcNow = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.ValidateForEnabledProvider();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public MovieMetadataProviderDescriptor Descriptor { get; } = new(Provider, "TMDB", [MovieNamespace], MappingVersion);

    public async ValueTask<MovieProviderResult> SearchAsync(MovieSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TitleCandidate)) return new MovieProviderInvalidResponse("A movie title is required.");
        var query = $"search/movie?query={Uri.EscapeDataString(request.TitleCandidate)}&language={Uri.EscapeDataString(options.Language)}";
        if (request.Year is int year) query += $"&primary_release_year={year}";
        var response = await SendAsync(query, cancellationToken);
        if (response.Result is not null) return response.Result;
        using var message = response.Message!;
        try
        {
            var dto = await JsonSerializer.DeserializeAsync<TmdbSearchResponse>(await message.Content.ReadAsStreamAsync(cancellationToken), Json, cancellationToken);
            if (dto?.Results is null) return new MovieProviderInvalidResponse("TMDB search response is structurally invalid.");
            var candidates = new List<MovieSearchCandidate>();
            foreach (var movie in dto.Results)
            {
                if (movie.Id <= 0 || string.IsNullOrWhiteSpace(movie.Title)) return new MovieProviderInvalidResponse("TMDB search response contains an invalid movie.");
                var date = ParseDate(movie.ReleaseDate, out var invalidDate);
                if (invalidDate) return new MovieProviderInvalidResponse("TMDB search response contains an invalid release date.");
                candidates.Add(new MovieSearchCandidate(new ExternalIdentifier(Provider, MovieNamespace, movie.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), movie.Title, EmptyToNull(movie.OriginalTitle), date, movie.Popularity, null, null));
            }
            return new MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>(candidates);
        }
        catch (JsonException) { return new MovieProviderInvalidResponse("TMDB returned malformed JSON."); }
    }

    public async ValueTask<MovieProviderResult> GetMovieAsync(ExternalIdentifier identifier, CancellationToken cancellationToken = default)
    {
        if (identifier.Provider != Provider || identifier.Namespace != MovieNamespace || !long.TryParse(identifier.Value, out var id) || id <= 0)
            return new MovieProviderUnsupported("The identifier is not a TMDB movie identifier.");
        var response = await SendAsync($"movie/{id}?language={Uri.EscapeDataString(options.Language)}", cancellationToken);
        if (response.Result is not null) return response.Result;
        using var message = response.Message!;
        try
        {
            var movie = await JsonSerializer.DeserializeAsync<TmdbMovieDetails>(await message.Content.ReadAsStreamAsync(cancellationToken), Json, cancellationToken);
            if (movie is null || movie.Id != id || string.IsNullOrWhiteSpace(movie.Title)) return new MovieProviderInvalidResponse("TMDB details response is structurally invalid.");
            var date = ParseDate(movie.ReleaseDate, out var invalidDate);
            if (invalidDate) return new MovieProviderInvalidResponse("TMDB details response contains an invalid release date.");
            var fields = new List<NormalizedMetadataField> { new(new MetadataField("title"), MetadataValue.Text(movie.Title), null) };
            AddText(fields, "original-title", movie.OriginalTitle);
            AddText(fields, "description", movie.Overview);
            if (date is not null) fields.Add(new(new MetadataField("release-date"), MetadataValue.Date(date.Value), null));
            var artwork = new List<NormalizedArtworkReference>();
            AddArtwork(artwork, "poster", movie.PosterPath);
            AddArtwork(artwork, "backdrop", movie.BackdropPath);
            var observedAt = utcNow();
            var document = new NormalizedMovieDocument(identifier, [], fields, artwork, observedAt, MappingVersion, Fingerprint(identifier, fields, artwork));
            return new MovieProviderSuccess<NormalizedMovieDocument>(document);
        }
        catch (JsonException) { return new MovieProviderInvalidResponse("TMDB returned malformed JSON."); }
    }

    private async ValueTask<(HttpResponseMessage? Message, MovieProviderResult? Result)> SendAsync(string relativeUri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            var message = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (message.IsSuccessStatusCode) return (message, null);
            MovieProviderResult result = message.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new MovieProviderUnsupported("TMDB authorization failed."),
                HttpStatusCode.NotFound => new MovieProviderUnsupported("TMDB movie was not found."),
                (HttpStatusCode)429 => new MovieProviderUnavailable("TMDB rate limit reached."),
                _ when (int)message.StatusCode >= 500 => new MovieProviderUnavailable("TMDB is unavailable."),
                _ => new MovieProviderInvalidResponse("TMDB returned an unexpected HTTP response.")
            };
            message.Dispose();
            return (null, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return (null, new MovieProviderCancelled()); }
        catch (OperationCanceledException) { return (null, new MovieProviderTimeout("TMDB request timed out.")); }
        catch (HttpRequestException) { return (null, new MovieProviderUnavailable("TMDB is unavailable.")); }
    }

    private static DateOnly? ParseDate(string? value, out bool invalid)
    {
        invalid = !string.IsNullOrWhiteSpace(value) && !DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
        return invalid || string.IsNullOrWhiteSpace(value) ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static void AddText(List<NormalizedMetadataField> fields, string code, string? value) { if (EmptyToNull(value) is { } text) fields.Add(new(new MetadataField(code), MetadataValue.Text(text), null)); }
    private static void AddArtwork(List<NormalizedArtworkReference> artwork, string role, string? path) { if (EmptyToNull(path) is { } value) artwork.Add(new(new ArtworkRole(role), new ArtworkLocator("tmdb-image", value), null)); }

    private static string Fingerprint(ExternalIdentifier id, IEnumerable<NormalizedMetadataField> fields, IEnumerable<NormalizedArtworkReference> artwork)
    {
        var canonical = new StringBuilder(MappingVersion).Append('\n').Append(id.Provider.Value).Append('|').Append(id.Namespace.Value).Append('|').Append(id.Value).Append('\n');
        foreach (var field in fields.OrderBy(x => x.Field.Code, StringComparer.Ordinal)) canonical.Append("f|").Append(field.Field.Code).Append('|').Append(field.Value.Kind).Append('|').Append(field.Value.TextValue ?? field.Value.DateValue?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "").Append('\n');
        foreach (var image in artwork.OrderBy(x => x.Role.Code, StringComparer.Ordinal).ThenBy(x => x.Locator.OpaqueValue, StringComparer.Ordinal)) canonical.Append("a|").Append(image.Role.Code).Append('|').Append(image.Locator.Scheme).Append('|').Append(image.Locator.OpaqueValue).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private sealed class TmdbSearchResponse { [JsonPropertyName("page")] public int Page { get; init; } [JsonPropertyName("results")] public List<TmdbSearchMovie>? Results { get; init; } }
    private sealed class TmdbSearchMovie { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("title")] public string? Title { get; init; } [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; } [JsonPropertyName("overview")] public string? Overview { get; init; } [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; } [JsonPropertyName("poster_path")] public string? PosterPath { get; init; } [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; } [JsonPropertyName("popularity")] public decimal? Popularity { get; init; } }
    private sealed class TmdbMovieDetails { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("title")] public string? Title { get; init; } [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; } [JsonPropertyName("overview")] public string? Overview { get; init; } [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; } [JsonPropertyName("poster_path")] public string? PosterPath { get; init; } [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; } }
}
