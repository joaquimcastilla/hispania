using Hispania.Library.Contracts;
using Hispania.Media.Domain.Sources;

namespace Hispania.Library.Application;

public static class LibrarySourceLocator
{
    public static SourceLocator From(LibraryFileReference file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new SourceLocator(new SourceScheme("library"), $"{file.RootKey}/{file.RelativePath}");
    }
}
