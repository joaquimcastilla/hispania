using Hispania.Media.Domain.Sources;

namespace Hispania.Library.Contracts;

public static class LibrarySourceLocator
{
    private const string Scheme = "library";

    public static SourceLocator From(LibraryFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SourceLocator(new SourceScheme(Scheme), $"{file.RootKey}/{file.RelativePath}");
    }

    public static bool TryGetFileReference(SourceLocator locator, out LibraryFileReference? file)
    {
        ArgumentNullException.ThrowIfNull(locator);
        file = null;

        if (!string.Equals(locator.Scheme.Value, Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = locator.OpaqueValue.IndexOf('/');
        if (separator <= 0 || separator == locator.OpaqueValue.Length - 1)
        {
            return false;
        }

        try
        {
            file = new LibraryFileReference(locator.OpaqueValue[..separator], locator.OpaqueValue[(separator + 1)..]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
