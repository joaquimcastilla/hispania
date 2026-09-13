namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record ContainerFormat
{
    public ContainerFormat(string @namespace, string code)
    {
        Namespace = Normalize(@namespace, "container-format.namespace.invalid", "A container format namespace must be a bounded lower-case code.");
        Code = Normalize(code, "container-format.code.invalid", "A container format code must be a bounded lower-case code.");
    }

    public string Namespace { get; }

    public string Code { get; }

    private static string Normalize(string? value, string errorCode, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 128)
        {
            throw new DomainValidationException(errorCode, message);
        }

        var normalized = value.ToLowerInvariant();
        if (normalized[0] is < 'a' or > 'z' || !normalized.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_'))
        {
            throw new DomainValidationException(errorCode, message);
        }

        return normalized;
    }
}
