using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class MovieMetadataAutoMatcherTests
{
    private static readonly ExternalIdentifierProvider Provider = new("tmdb");
    private static readonly ExternalIdentifierNamespace Movie = new("movie");

    [Fact]
    public void ParsesYearAndTitleFromFilename()
    {
        var result = MovieLocalSearchSignalParser.Parse("The Weather Man (2005).avi");
        Assert.Equal("The Weather Man", result.Query);
        Assert.Equal(2005, result.Year);
    }

    [Theory]
    [InlineData("Seduction and Snacks (2021).mp4", "Seduction and Snacks", 2021)]
    [InlineData("Un Ratoncito Duro De Roer (1997).mkv", "Un Ratoncito Duro De Roer", 1997)]
    public void ParsesSupportedLibraryNamesWithoutUsingTheExtensionAsIdentity(string fileName, string title, int year)
    {
        var result = MovieLocalSearchSignalParser.Parse(fileName);
        Assert.Equal(title, result.Query);
        Assert.Equal(year, result.Year);
    }

    [Fact]
    public void ExactYearAndOriginalTitleCanConfirmLocalizedFilename()
    {
        var candidate = new MovieSearchCandidate(new ExternalIdentifier(Provider, Movie, "mouse-hunt"), "Un ratoncito duro de roer", "Mouse Hunt", new DateOnly(1997, 1, 1), null, null, null);
        var result = new MovieMetadataAutoMatcher().Match(new("Mouse Hunt", 1997), [candidate]);
        Assert.IsType<MovieAutoMatchResult.Confirmed>(result);
    }

    [Fact]
    public void ExactTitleAndYearWithOneCandidateConfirms()
    {
        var result = new MovieMetadataAutoMatcher().Match(new("The Weather Man", 2005), [Candidate("The Weather Man", 2005)]);
        Assert.IsType<MovieAutoMatchResult.Confirmed>(result);
    }

    [Fact]
    public void AmericanBeautyMatchesItsFirstWorldwideReleaseYear()
    {
        var candidate = Candidate("American Beauty", 1999) with { ReleaseDate = new DateOnly(1999, 9, 15) };
        Assert.IsType<MovieAutoMatchResult.Confirmed>(new MovieMetadataAutoMatcher().Match(new("American Beauty", 1999), [candidate]));
    }

    [Fact]
    public void WrongYearDoesNotConfirm()
    {
        var result = new MovieMetadataAutoMatcher().Match(new("The Weather Man", 2005), [Candidate("The Weather Man", 2006)]);
        Assert.IsType<MovieAutoMatchResult.NoMatch>(result);
    }

    [Fact]
    public void WrongTitleDoesNotConfirm()
    {
        var result = new MovieMetadataAutoMatcher().Match(new("The Weather Man", 2005), [Candidate("The Weather House", 2005)]);
        Assert.IsType<MovieAutoMatchResult.NoMatch>(result);
    }

    [Fact]
    public void MultipleExactCandidatesAreAmbiguous()
    {
        var result = new MovieMetadataAutoMatcher().Match(new("The Weather Man", 2005), [Candidate("The Weather Man", 2005), Candidate("The Weather Man", 2005)]);
        Assert.IsType<MovieAutoMatchResult.Ambiguous>(result);
    }

    [Fact]
    public void MissingYearDoesNotConfirm()
    {
        var result = new MovieMetadataAutoMatcher().Match(new("The Weather Man", null), [Candidate("The Weather Man", 2005)]);
        Assert.IsType<MovieAutoMatchResult.NoMatch>(result);
    }

    private static MovieSearchCandidate Candidate(string title, int year) => new(new ExternalIdentifier(Provider, Movie, Guid.NewGuid().ToString()), title, null, new DateOnly(year, 1, 1), null, null, null);
}
