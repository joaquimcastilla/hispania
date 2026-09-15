using System.Net.Http.Headers;
using System.Globalization;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;

if (args.Length == 0 || !string.Equals(args[0], "tmdb-smoke", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage: tmdb-smoke [--id <tmdb-movie-id>] [--query <title>] [--year <year>] [--language <language>] [--region <region>]");
    return;
}

var arguments = ReadArguments(args[1..]);
var token = Environment.GetEnvironmentVariable("Metadata__Tmdb__ReadAccessToken");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Metadata__Tmdb__ReadAccessToken must be set in the environment before running tmdb-smoke.");
    return;
}

var language = arguments.GetValueOrDefault("language") ?? "es-ES";
var region = arguments.GetValueOrDefault("region") ?? "ES";
var query = arguments.GetValueOrDefault("query") ?? "Seduction and Snacks";
var year = int.TryParse(arguments.GetValueOrDefault("year") ?? "2021", out var parsedYear) ? parsedYear : throw new ArgumentException("--year must be an integer.");
var options = new TmdbMovieMetadataProviderOptions { Enabled = true, ReadAccessToken = token, Language = language, Region = region };
using var client = new HttpClient { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
var provider = new TmdbMovieMetadataProvider(client, options);

var search = await provider.SearchAsync(new MovieSearchRequest(query, year, null));
if (search is not MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>> candidates)
{
    Console.Error.WriteLine($"TMDB search failed: {search.GetType().Name}.");
    return;
}

foreach (var candidate in candidates.Value)
{
    Console.WriteLine($"TMDB ID: {candidate.ExternalIdentifier.Value}");
    Console.WriteLine($"Title: {candidate.Title}");
    Console.WriteLine($"Original title: {candidate.OriginalTitle ?? ""}");
    Console.WriteLine($"Release date: {candidate.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""}");
    Console.WriteLine();
}

if (!arguments.TryGetValue("id", out var selectedId) || string.IsNullOrWhiteSpace(selectedId)) return;
var details = await provider.GetMovieAsync(new ExternalIdentifier(TmdbMovieMetadataProvider.Provider, TmdbMovieMetadataProvider.MovieNamespace, selectedId));
if (details is not MovieProviderSuccess<NormalizedMovieDocument> document)
{
    Console.Error.WriteLine($"TMDB detail fetch failed: {details.GetType().Name}.");
    return;
}

Console.WriteLine("Normalized document:");
foreach (var field in document.Value.Fields)
    Console.WriteLine($"{field.Field.Code}: {field.Value.TextValue ?? field.Value.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""}");
foreach (var artwork in document.Value.Artwork)
    Console.WriteLine($"{artwork.Role.Code} locator: {artwork.Locator.Scheme}:{artwork.Locator.OpaqueValue}");
Console.WriteLine($"Mapping version: {document.Value.MappingVersion}");
Console.WriteLine($"Fingerprint: {document.Value.PayloadFingerprint}");

static Dictionary<string, string> ReadArguments(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            throw new ArgumentException("Arguments must be supplied as --name value.");
        values.Add(arguments[index][2..], arguments[index + 1]);
    }
    return values;
}
