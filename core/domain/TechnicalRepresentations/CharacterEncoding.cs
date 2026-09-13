namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record CharacterEncoding
{
    public CharacterEncoding(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code != code.Trim() || code.Length > 128)
        {
            throw new DomainValidationException("character-encoding.invalid", "A character encoding must be a bounded lower-case code.");
        }

        var normalized = code.ToLowerInvariant();
        if (normalized[0] is < 'a' or > 'z' || !normalized.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_'))
        {
            throw new DomainValidationException("character-encoding.invalid", "A character encoding must be a bounded lower-case code.");
        }

        Code = normalized;
    }

    public string Code { get; }
}
