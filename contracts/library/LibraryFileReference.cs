namespace Hispania.Library.Contracts;

public sealed record LibraryFileReference
{
    public LibraryFileReference(string rootKey, string relativePath)
    {
        var importKey = new ImportKey(rootKey, relativePath);
        RootKey = importKey.RootKey;
        RelativePath = importKey.NormalizedRelativePath;
    }

    public string RootKey { get; }

    public string RelativePath { get; }
}
