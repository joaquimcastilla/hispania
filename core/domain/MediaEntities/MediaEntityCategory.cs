namespace Hispania.Media.Domain.MediaEntities;

public sealed record MediaEntityCategory
{
    private MediaEntityCategory(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static MediaEntityCategory MovieRealisation { get; } = new("movie-realisation");

    public static MediaEntityCategory AudioRecording { get; } = new("audio-recording");

    public static MediaEntityCategory MusicVideo { get; } = new("music-video");

    public static MediaEntityCategory EpisodeRealisation { get; } = new("episode-realisation");

    public static MediaEntityCategory From(string code)
    {
        if (!IsValidCode(code))
        {
            throw new DomainValidationException("media-entity-category.invalid", "A MediaEntity category code must be a lower-case hyphenated code.");
        }

        return new MediaEntityCategory(code);
    }

    private static bool IsValidCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code != code.Trim())
        {
            return false;
        }

        if (code[0] is < 'a' or > 'z')
        {
            return false;
        }

        return code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }
}
