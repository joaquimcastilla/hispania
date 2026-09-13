namespace Hispania.Media.Domain;

public sealed class DomainValidationException : Exception
{
    public DomainValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
