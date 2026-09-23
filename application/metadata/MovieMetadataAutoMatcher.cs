using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hispania.Metadata.Contracts;

namespace Hispania.Metadata.Application;

public sealed record MovieLocalSearchSignals(string Query, int? Year);

public static class MovieLocalSearchSignalParser
{
    private static readonly Regex YearSuffix = new(@"^(?<title>.+?)\s*\((?<year>\d{4})\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static MovieLocalSearchSignals Parse(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var match = YearSuffix.Match(Path.GetFileNameWithoutExtension(displayName).Trim());
        if (!match.Success) return new MovieLocalSearchSignals(Path.GetFileNameWithoutExtension(displayName).Trim(), null);
        return new MovieLocalSearchSignals(match.Groups["title"].Value.Trim(), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture));
    }
}

public abstract record MovieAutoMatchResult
{
    public sealed record Confirmed(MovieSearchCandidate Candidate) : MovieAutoMatchResult;
    public sealed record Ambiguous(IReadOnlyCollection<MovieSearchCandidate> Candidates) : MovieAutoMatchResult;
    public sealed record NoMatch : MovieAutoMatchResult;
}

public interface IMovieMetadataAutoMatcher
{
    MovieAutoMatchResult Match(MovieLocalSearchSignals signals, IReadOnlyCollection<MovieSearchCandidate> candidates);
}

public sealed class MovieMetadataAutoMatcher : IMovieMetadataAutoMatcher
{
    public MovieAutoMatchResult Match(MovieLocalSearchSignals signals, IReadOnlyCollection<MovieSearchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(candidates);
        if (signals.Year is null) return new MovieAutoMatchResult.NoMatch();
        var exact = candidates.Where(candidate => candidate.ReleaseDate?.Year == signals.Year && (TitlesEqual(candidate.Title, signals.Query) || candidate.OriginalTitle is not null && TitlesEqual(candidate.OriginalTitle, signals.Query))).ToArray();
        return exact.Length switch
        {
            1 => new MovieAutoMatchResult.Confirmed(exact[0]),
            > 1 => new MovieAutoMatchResult.Ambiguous(exact),
            _ => new MovieAutoMatchResult.NoMatch()
        };
    }

    private static bool TitlesEqual(string left, string right) => Normalize(left) == Normalize(right);

    private static string Normalize(string value)
    {
        var form = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(form.Length);
        foreach (var character in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).Replace("&", "and", StringComparison.Ordinal).ToUpperInvariant();
    }
}
