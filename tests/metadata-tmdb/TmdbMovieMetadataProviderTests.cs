using System.Net;
using System.Text;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Metadata.Tmdb;
using Xunit;

namespace Hispania.Metadata.Tmdb.Tests;

public sealed class TmdbMovieMetadataProviderTests
{
    [Fact]
    public async Task SearchEncodesParametersSendsBearerAndMapsCandidates()
    {
        HttpRequestMessage? captured = null;
        var provider = Provider(request => { captured = request; return Json(HttpStatusCode.OK, """{"page":1,"results":[{"id":12,"title":"Título","original_title":"Original","release_date":"2021-05-04","popularity":4.5},{"id":13,"title":"Otro","release_date":""}]}"""); });
        var result = Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await provider.SearchAsync(new("A & B", 2021, TimeSpan.FromMinutes(90))));
        Assert.Equal("/3/search/movie", captured!.RequestUri!.AbsolutePath);
        Assert.Contains("query=A%20%26%20B", captured.RequestUri.Query);
        Assert.Contains("primary_release_year=2021", captured.RequestUri.Query);
        Assert.Contains("language=es-ES", captured.RequestUri.Query);
        Assert.DoesNotContain("region=ES", captured.RequestUri.Query);
        Assert.Contains("include_adult=false", captured.RequestUri.Query);
        Assert.Contains("page=1", captured.RequestUri.Query);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("fake-token", captured.Headers.Authorization.Parameter);
        var candidate = Assert.Single(result.Value, x => x.ExternalIdentifier.Value == "12");
        Assert.Equal("Original", candidate.OriginalTitle); Assert.Equal(new DateOnly(2021, 5, 4), candidate.ReleaseDate); Assert.Equal(4.5m, candidate.ProviderScore); Assert.Null(candidate.ProviderConfidence);
    }

    [Fact]
    public async Task SearchUsesThePrimaryWorldwideReleaseDateInsteadOfTheConfiguredLocalRegion()
    {
        HttpRequestMessage? captured = null;
        var provider = Provider(request => { captured = request; return Json(HttpStatusCode.OK, """{"page":1,"results":[{"id":14,"title":"American Beauty","original_title":"American Beauty","release_date":"1999-09-15"}]}"""); });
        var result = Assert.IsType<MovieProviderSuccess<IReadOnlyCollection<MovieSearchCandidate>>>(await provider.SearchAsync(new("American Beauty", 1999, null)));
        var candidate = Assert.Single(result.Value);
        Assert.Equal("14", candidate.ExternalIdentifier.Value);
        Assert.Equal(new DateOnly(1999, 9, 15), candidate.ReleaseDate);
        Assert.DoesNotContain("region=ES", captured!.RequestUri!.Query);
        Assert.Contains("primary_release_year=1999", captured.RequestUri.Query);
    }

    [Fact]
    public async Task DetailsMapsNormalizedDocumentArtworkAndCanonicalFingerprint()
    {
        var payload = """{"backdrop_path":"/back.jpg","overview":"Description","title":"Title","id":12,"original_title":"Original","poster_path":"/poster.jpg","release_date":"2021-05-04"}""";
        var provider = Provider(_ => Json(HttpStatusCode.OK, payload), () => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var id = new ExternalIdentifier(new("tmdb"), new("movie"), "12");
        var result = Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await provider.GetMovieAsync(id));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), result.Value.ObservedAt);
        Assert.Equal("tmdb-movie-v2", result.Value.MappingVersion);
        Assert.Equal(4, result.Value.Fields.Count); Assert.Equal(2, result.Value.Artwork.Count);
        Assert.All(result.Value.Artwork, x => Assert.Equal("tmdb-image", x.Locator.Scheme));
        var reordered = Provider(_ => Json(HttpStatusCode.OK, """{ "title":"Title", "id":12, "release_date":"2021-05-04", "poster_path":"/poster.jpg", "backdrop_path":"/back.jpg", "overview":"Description", "original_title":"Original" }"""), () => DateTimeOffset.UtcNow);
        var same = Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await reordered.GetMovieAsync(id));
        Assert.Equal(result.Value.PayloadFingerprint, same.Value.PayloadFingerprint);
    }

    [Fact]
    public async Task DetailsMapsCastCrewRuntimeAndRegionalCertificationIntoNormalizedMetadata()
    {
        var payload = """{"id":12,"title":"Title","runtime":155,"release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"R"}]},{"iso_3166_1":"ES","release_dates":[{"certification":"12"}]}]},"credits":{"cast":[{"id":1,"name":"Actor One","character":"Hero","order":0,"profile_path":"/actor.jpg"}],"crew":[{"id":2,"name":"Director One","department":"Directing","job":"Director","profile_path":"/director.jpg"}]}}""";
        var result = Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await Provider(_ => Json(HttpStatusCode.OK, payload)).GetMovieAsync(new(new("tmdb"), new("movie"), "12")));

        Assert.Equal("tmdb-movie-v2", result.Value.MappingVersion);
        Assert.Equal(155, Assert.Single(result.Value.Fields, x => x.Field.Code == "runtime-minutes").Value.IntegerValue);
        Assert.Equal("12", Assert.Single(result.Value.Fields, x => x.Field.Code == "content-rating").Value.TextValue);
        Assert.Contains(result.Value.RichMetadata!.People, x => x.Name == "Actor One" && x.ProfileArtworkLocator == "/actor.jpg");
        Assert.Contains(result.Value.RichMetadata.People, x => x.Name == "Director One");
        Assert.Contains(result.Value.RichMetadata.Credits, x => x.PersonName == "Actor One" && x.CreditType == "cast" && x.Character == "Hero");
        Assert.Contains(result.Value.RichMetadata.Credits, x => x.PersonName == "Director One" && x.CreditType == "director" && x.Job == "Director");
    }

    [Fact]
    public async Task PersonDetailsFetchesTmdbByKnownPersonIdAndMapsLocalizedProfile()
    {
        HttpRequestMessage? captured = null;
        var provider = Provider(request => { captured = request; return Json(HttpStatusCode.OK, """{"id":142115,"name":"Kiana Madeira","biography":"Biografía","birthday":"1992-11-04","deathday":null,"place_of_birth":"Toronto, Canada","profile_path":"/kiana.jpg"}"""); });
        var profile = await provider.GetPersonAsync("142115");
        Assert.Equal("/3/person/142115", captured!.RequestUri!.AbsolutePath);
        Assert.Contains("language=es-ES", captured.RequestUri.Query);
        Assert.NotNull(profile);
        Assert.Equal("Kiana Madeira", profile.Name);
        Assert.Equal("Biografía", profile.Biography);
        Assert.Equal(new DateOnly(1992, 11, 4), profile.Birthday);
        Assert.Equal("Toronto, Canada", profile.PlaceOfBirth);
        Assert.Equal("/kiana.jpg", profile.ProfileArtworkLocator);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(MovieProviderUnsupported))]
    [InlineData(HttpStatusCode.Forbidden, typeof(MovieProviderUnsupported))]
    [InlineData(HttpStatusCode.NotFound, typeof(MovieProviderUnsupported))]
    [InlineData((HttpStatusCode)429, typeof(MovieProviderUnavailable))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(MovieProviderUnavailable))]
    public async Task HttpErrorsAreMappedWithoutSecrets(HttpStatusCode status, Type expected)
    {
        var result = await Provider(_ => new HttpResponseMessage(status)).SearchAsync(new("Title", null, null));
        Assert.IsType(expected, result); Assert.DoesNotContain("fake-token", result.ToString());
    }

    [Fact]
    public async Task InvalidJsonDatesAndIdentifiersAreRejected()
    {
        Assert.IsType<MovieProviderInvalidResponse>(await Provider(_ => Json(HttpStatusCode.OK, "not json")).SearchAsync(new("Title", null, null)));
        Assert.IsType<MovieProviderInvalidResponse>(await Provider(_ => Json(HttpStatusCode.OK, """{"id":12,"title":"Title","release_date":"not-a-date"}""")).GetMovieAsync(new(new("tmdb"), new("movie"), "12")));
        Assert.IsType<MovieProviderUnsupported>(await Provider(_ => Json(HttpStatusCode.OK, "{}" )).GetMovieAsync(new(new("imdb"), new("movie"), "12")));
    }

    [Fact]
    public async Task TimeoutAndCancellationAreDistinguished()
    {
        var timeout = await Provider(_ => throw new OperationCanceledException()).SearchAsync(new("Title", null, null));
        Assert.IsType<MovieProviderTimeout>(timeout);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(_ => throw new OperationCanceledException()).SearchAsync(new("Title", null, null), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task EmptyDetailsFieldsAndArtworkAreOmitted()
    {
        var result = Assert.IsType<MovieProviderSuccess<NormalizedMovieDocument>>(await Provider(_ => Json(HttpStatusCode.OK, """{"id":12,"title":"Title","original_title":"","overview":"","release_date":"","poster_path":null,"backdrop_path":null}""")).GetMovieAsync(new(new("tmdb"), new("movie"), "12")));
        Assert.Single(result.Value.Fields);
        Assert.Empty(result.Value.Artwork);
    }

    private static TmdbMovieMetadataProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> send, Func<DateTimeOffset>? now = null)
    {
        var client = new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fake-token");
        return new TmdbMovieMetadataProvider(client, new TmdbMovieMetadataProviderOptions { Enabled = true, ReadAccessToken = "fake-token" }, now);
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request)); }
}
