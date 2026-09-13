namespace Hispania.Library.Contracts;

/// <summary>
/// Identifies a local library import slot. It is deliberately distinct from Media Domain identity.
/// </summary>
public sealed record ImportKey
{
    public ImportKey(string rootKey, string normalizedRelativePath)
    {
        LibraryPathValidation.ValidateRootKey(rootKey, nameof(rootKey));
        NormalizedRelativePath = LibraryPathValidation.NormalizeRelativePath(normalizedRelativePath, nameof(normalizedRelativePath));
        RootKey = rootKey;
    }

    public string RootKey { get; }

    public string NormalizedRelativePath { get; }
}

internal static class LibraryPathValidation
{
    public static void ValidateRootKey(string rootKey, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(rootKey) || rootKey != rootKey.Trim() || rootKey.Length > 128 || rootKey.Any(char.IsControl) || rootKey.Contains('/') || rootKey.Contains('\\'))
        {
            throw new ArgumentException("A library root key must be a bounded, non-empty key without path separators.", parameterName);
        }
    }

    public static string NormalizeRelativePath(string relativePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath != relativePath.Trim() || relativePath.Length > 4096 || relativePath.Any(char.IsControl) || IsAbsolute(relativePath))
        {
            throw new ArgumentException("A library file reference must use a bounded relative path.", parameterName);
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException("A library file reference cannot contain empty, current, or parent path segments.", parameterName);
        }

        return string.Join('/', segments);
    }

    private static bool IsAbsolute(string path) =>
        path[0] is '/' or '\\' ||
        (path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':');

    private static bool IsAsciiLetter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
