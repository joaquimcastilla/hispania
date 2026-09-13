namespace Hispania.Media.Domain.Works;

public sealed record WorkCategory
{
    private WorkCategory(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static WorkCategory Movie { get; } = new("movie");

    public static WorkCategory Series { get; } = new("series");

    public static WorkCategory Episode { get; } = new("episode");

    public static WorkCategory MusicalWork { get; } = new("musical-work");

    public static WorkCategory From(string code)
    {
        if (!IsValidCode(code))
        {
            throw new DomainValidationException("work-category.invalid", "A Work category code must be a lower-case hyphenated code.");
        }

        return new WorkCategory(code);
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
