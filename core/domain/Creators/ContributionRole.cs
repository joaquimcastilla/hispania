namespace Hispania.Media.Domain.Creators;

public sealed record ContributionRole
{
    public ContributionRole(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainValidationException("contribution-role.code.required", "A contribution role code is required.");
        }

        if (code.Any(char.IsWhiteSpace))
        {
            throw new DomainValidationException("contribution-role.code.invalid", "A contribution role code cannot contain whitespace.");
        }

        var normalizedCode = code.ToLowerInvariant();
        if (normalizedCode.Length > 64 || !IsValidCode(normalizedCode))
        {
            throw new DomainValidationException("contribution-role.code.invalid", "A contribution role code must use lowercase letters, digits, and hyphens, beginning with a letter.");
        }

        Code = normalizedCode;
    }

    public string Code { get; }

    private static bool IsValidCode(string code)
    {
        if (code.Length == 0 || code[0] is < 'a' or > 'z')
        {
            return false;
        }

        return code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }
}
