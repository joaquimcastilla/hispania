namespace Hispania.Library.Contracts;

public sealed record LibraryFileReference
{
    public LibraryFileReference(string rootKey, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootKey) || rootKey != rootKey.Trim() || rootKey.Length > 128 || rootKey.Any(char.IsControl) || rootKey.Contains('/') || rootKey.Contains('\\'))
        {
            throw new ArgumentException("A library root key must be a bounded, non-empty key without path separators.", nameof(rootKey));
        }

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath != relativePath.Trim() || relativePath.Length > 4096 || relativePath.Any(char.IsControl) || IsAbsolute(relativePath))
        {
            throw new ArgumentException("A library file reference must use a bounded relative path.", nameof(relativePath));
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("A library file reference cannot contain empty, current, or parent path segments.", nameof(relativePath));
        }

        RootKey = rootKey;
        RelativePath = string.Join('/', segments);
    }

    public string RootKey { get; }

    public string RelativePath { get; }

    private static bool IsAbsolute(string path) =>
        path[0] is '/' or '\\' ||
        (path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':');

    private static bool IsAsciiLetter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
