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
public sealed class TmdbMovieMetadataProvider : IMovieMetadataProvider, IPersonMetadataProvider
{
    public static readonly ExternalIdentifierProvider Provider = new("tmdb");
    public static readonly ExternalIdentifierNamespace MovieNamespace = new("movie");
    private const string MappingVersion = "tmdb-movie-v2";
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

    public async ValueTask<NormalizedPerson?> GetPersonAsync(string providerPersonId, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(providerPersonId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
        var response = await SendAsync($"person/{id}?language={Uri.EscapeDataString(options.Language)}", cancellationToken);
        if (response.Result is not null) return null;
        using var message = response.Message!;
        try
        {
            var person = await JsonSerializer.DeserializeAsync<TmdbPersonDetails>(await message.Content.ReadAsStreamAsync(cancellationToken), Json, cancellationToken);
            if (person is null || person.Id != id || string.IsNullOrWhiteSpace(person.Name)) return null;
            return new NormalizedPerson(id.ToString(System.Globalization.CultureInfo.InvariantCulture), person.Name!, person.ProfilePath, Provider.Value, Biography: EmptyToNull(person.Biography), Birthday: ParseDate(person.Birthday, out _), Deathday: ParseDate(person.Deathday, out _), PlaceOfBirth: EmptyToNull(person.PlaceOfBirth));
        }
        catch (JsonException) { return null; }
    }

    public async ValueTask<MovieProviderResult> SearchAsync(MovieSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TitleCandidate)) return new MovieProviderInvalidResponse("A movie title is required.");
        // Do not pass the user's UI region here: TMDB then substitutes a local release date
        // (for example Spain's 2000 date) for the movie's primary worldwide release date.
        var query = $"search/movie?query={Uri.EscapeDataString(request.TitleCandidate)}&language={Uri.EscapeDataString(options.Language)}&include_adult=false&page=1";
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
        var response = await SendAsync($"movie/{id}?language={Uri.EscapeDataString(options.Language)}&append_to_response=keywords,credits,external_ids,release_dates", cancellationToken);
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
            AddText(fields, "original-language", movie.OriginalLanguage);
            AddText(fields, "description", movie.Overview);
            AddText(fields, "tagline", movie.Tagline);
            if (date is not null) fields.Add(new(new MetadataField("release-date"), MetadataValue.Date(date.Value), null));
            if (movie.RuntimeMinutes is > 0) fields.Add(new(new MetadataField("runtime-minutes"), MetadataValue.Integer(movie.RuntimeMinutes.Value), null));
            if (PreferredCertification(movie.ReleaseDates) is { } certification) AddText(fields, "content-rating", certification);
            if (movie.VoteAverage is decimal rating) fields.Add(new(new MetadataField("community-rating"), MetadataValue.Decimal(rating), null));
            foreach (var genre in movie.Genres ?? []) AddText(fields, "genre", genre.Name);
            foreach (var company in movie.ProductionCompanies ?? []) AddText(fields, "studio", company.Name);
            foreach (var keyword in movie.Keywords?.Results ?? []) AddText(fields, "keyword", keyword.Name);
            AddText(fields, "imdb-id", movie.ImdbId);
            AddText(fields, "tmdb-collection-id", movie.BelongsToCollection?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var artwork = new List<NormalizedArtworkReference>();
            AddArtwork(artwork, "poster", movie.PosterPath);
            AddArtwork(artwork, "backdrop", movie.BackdropPath);
            var observedAt = utcNow();
            var rich = new NormalizedMovieRichMetadata(
                (movie.Genres ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                (movie.Keywords?.Results ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                (movie.ProductionCompanies ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => new NormalizedStudio(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name!)).ToArray(),
                (movie.Credits?.Cast ?? []).Concat(movie.Credits?.Crew ?? []).Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name)).Select(x => new NormalizedPerson(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name!, x.ProfilePath)).DistinctBy(x => x.ProviderId).ToArray(),
                (movie.Credits?.Cast ?? []).Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name)).Select(x => new NormalizedCredit(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name!, "cast", x.Character, null, x.Order, x.ProfilePath))
                    .Concat((movie.Credits?.Crew ?? []).Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name)).Select(x => new NormalizedCredit(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name!, CreditType(x.Department, x.Job), x.Character, x.Job, x.Order, x.ProfilePath)))
                    .ToArray());
            var secondary = new List<ExternalIdentifier>();
            if (EmptyToNull(movie.ImdbId) is { } imdb) secondary.Add(new ExternalIdentifier(new ExternalIdentifierProvider("imdb"), new ExternalIdentifierNamespace("movie"), imdb));
            if (movie.BelongsToCollection?.Id > 0) secondary.Add(new ExternalIdentifier(Provider, new ExternalIdentifierNamespace("collection"), movie.BelongsToCollection.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var document = new NormalizedMovieDocument(identifier, secondary, fields, artwork, observedAt, MappingVersion, Fingerprint(identifier, fields, artwork), rich);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (null, new MovieProviderTimeout("TMDB request timed out.")); }
        catch (HttpRequestException) { return (null, new MovieProviderUnavailable("TMDB is unavailable.")); }
    }

    private static DateOnly? ParseDate(string? value, out bool invalid)
    {
        invalid = !string.IsNullOrWhiteSpace(value) && !DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
        return invalid || string.IsNullOrWhiteSpace(value) ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string? PreferredCertification(TmdbReleaseDates? releaseDates)
    {
        var countries = releaseDates?.Results ?? [];
        return countries.FirstOrDefault(country => country.CountryCode == "ES")?.Releases?.FirstOrDefault(release => !string.IsNullOrWhiteSpace(release.Certification))?.Certification
            ?? countries.FirstOrDefault(country => country.CountryCode == "US")?.Releases?.FirstOrDefault(release => !string.IsNullOrWhiteSpace(release.Certification))?.Certification
            ?? countries.SelectMany(country => country.Releases ?? []).FirstOrDefault(release => !string.IsNullOrWhiteSpace(release.Certification))?.Certification;
    }
    private static void AddText(List<NormalizedMetadataField> fields, string code, string? value) { if (EmptyToNull(value) is { } text) fields.Add(new(new MetadataField(code), MetadataValue.Text(text), null)); }
    private static void AddArtwork(List<NormalizedArtworkReference> artwork, string role, string? path) { if (EmptyToNull(path) is { } value) artwork.Add(new(new ArtworkRole(role), new ArtworkLocator("tmdb-image", value), null)); }
    private static string CreditType(string? department, string? job) => job switch { "Director" => "director", "Screenplay" => "screenplay", "Writer" => "writer", "Producer" => "producer", _ when string.Equals(department, "Acting", StringComparison.OrdinalIgnoreCase) => "cast", _ => string.IsNullOrWhiteSpace(job) ? "crew" : job.Trim().ToLowerInvariant() };

    private static string Fingerprint(ExternalIdentifier id, IEnumerable<NormalizedMetadataField> fields, IEnumerable<NormalizedArtworkReference> artwork)
    {
        var canonical = new StringBuilder(MappingVersion).Append('\n').Append(id.Provider.Value).Append('|').Append(id.Namespace.Value).Append('|').Append(id.Value).Append('\n');
        foreach (var field in fields.OrderBy(x => x.Field.Code, StringComparer.Ordinal)) canonical.Append("f|").Append(field.Field.Code).Append('|').Append(field.Value.Kind).Append('|').Append(field.Value.TextValue ?? field.Value.DateValue?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "").Append('\n');
        foreach (var image in artwork.OrderBy(x => x.Role.Code, StringComparer.Ordinal).ThenBy(x => x.Locator.OpaqueValue, StringComparer.Ordinal)) canonical.Append("a|").Append(image.Role.Code).Append('|').Append(image.Locator.Scheme).Append('|').Append(image.Locator.OpaqueValue).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private sealed class TmdbSearchResponse { [JsonPropertyName("page")] public int Page { get; init; } [JsonPropertyName("results")] public List<TmdbSearchMovie>? Results { get; init; } }
    private sealed class TmdbSearchMovie { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("title")] public string? Title { get; init; } [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; } [JsonPropertyName("overview")] public string? Overview { get; init; } [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; } [JsonPropertyName("poster_path")] public string? PosterPath { get; init; } [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; } [JsonPropertyName("popularity")] public decimal? Popularity { get; init; } }
    private sealed class TmdbMovieDetails { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("title")] public string? Title { get; init; } [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; } [JsonPropertyName("original_language")] public string? OriginalLanguage { get; init; } [JsonPropertyName("overview")] public string? Overview { get; init; } [JsonPropertyName("tagline")] public string? Tagline { get; init; } [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; } [JsonPropertyName("runtime")] public int? RuntimeMinutes { get; init; } [JsonPropertyName("release_dates")] public TmdbReleaseDates? ReleaseDates { get; init; } [JsonPropertyName("vote_average")] public decimal? VoteAverage { get; init; } [JsonPropertyName("imdb_id")] public string? ImdbId { get; init; } [JsonPropertyName("belongs_to_collection")] public TmdbCollection? BelongsToCollection { get; init; } [JsonPropertyName("genres")] public List<TmdbNamed>? Genres { get; init; } [JsonPropertyName("production_companies")] public List<TmdbNamed>? ProductionCompanies { get; init; } [JsonPropertyName("keywords")] public TmdbKeywords? Keywords { get; init; } [JsonPropertyName("credits")] public TmdbCredits? Credits { get; init; } [JsonPropertyName("poster_path")] public string? PosterPath { get; init; } [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; } }
    private sealed class TmdbNamed { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("name")] public string? Name { get; init; } }
    private sealed class TmdbKeywords { [JsonPropertyName("keywords")] public List<TmdbNamed>? Results { get; init; } }
    private sealed class TmdbCollection { [JsonPropertyName("id")] public long Id { get; init; } }
    private sealed class TmdbCredits { [JsonPropertyName("cast")] public List<TmdbCredit>? Cast { get; init; } [JsonPropertyName("crew")] public List<TmdbCredit>? Crew { get; init; } }
    private sealed class TmdbReleaseDates { [JsonPropertyName("results")] public List<TmdbCountryReleaseDates>? Results { get; init; } }
    private sealed class TmdbCountryReleaseDates { [JsonPropertyName("iso_3166_1")] public string? CountryCode { get; init; } [JsonPropertyName("release_dates")] public List<TmdbReleaseDate>? Releases { get; init; } }
    private sealed class TmdbReleaseDate { [JsonPropertyName("certification")] public string? Certification { get; init; } }
    private sealed class TmdbCredit { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("name")] public string? Name { get; init; } [JsonPropertyName("character")] public string? Character { get; init; } [JsonPropertyName("department")] public string? Department { get; init; } [JsonPropertyName("job")] public string? Job { get; init; } [JsonPropertyName("order")] public int? Order { get; init; } [JsonPropertyName("profile_path")] public string? ProfilePath { get; init; } }
    private sealed class TmdbPersonDetails { [JsonPropertyName("id")] public long Id { get; init; } [JsonPropertyName("name")] public string? Name { get; init; } [JsonPropertyName("biography")] public string? Biography { get; init; } [JsonPropertyName("birthday")] public string? Birthday { get; init; } [JsonPropertyName("deathday")] public string? Deathday { get; init; } [JsonPropertyName("place_of_birth")] public string? PlaceOfBirth { get; init; } [JsonPropertyName("profile_path")] public string? ProfilePath { get; init; } }
}
