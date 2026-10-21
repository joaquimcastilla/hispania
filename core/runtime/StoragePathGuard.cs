namespace Hispania.Runtime;

/// <summary>
/// Fail-closed path checks for managed storage. These checks reject existing reparse
/// points in every path component; they do not provide handle-based TOCTOU protection.
/// </summary>
public static class StoragePathGuard
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string RequireOutside(string path, string protectedRoot, string errorMessage)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullProtectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
        EnsureNoReparsePoints(fullPath);
        EnsureNoReparsePoints(fullProtectedRoot);
        if (IsSameOrWithin(fullPath, fullProtectedRoot) || IsSameOrWithin(fullProtectedRoot, fullPath))
            throw new InvalidDataException(errorMessage);
        return fullPath;
    }

    public static string RequireWithin(string root, string path, string errorMessage)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        EnsureNoReparsePoints(fullRoot);
        EnsureNoReparsePoints(fullPath);
        if (!IsSameOrWithin(fullPath, fullRoot)) throw new InvalidDataException(errorMessage);
        return fullPath;
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("Storage path has no filesystem root.");
        var cursor = Path.TrimEndingDirectorySeparator(fullPath);
        var components = new Stack<string>();
        while (!string.Equals(cursor, root, PathComparison))
        {
            components.Push(cursor);
            var parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, PathComparison))
                throw new InvalidDataException("Storage path could not be verified.");
            cursor = Path.TrimEndingDirectorySeparator(parent);
        }

        var ordered = components.ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var component = ordered[index];
            FileAttributes attributes;
            try { attributes = File.GetAttributes(component); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Storage path contains a reparse point.");
            if (index < ordered.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Storage path contains a non-directory ancestor.");
        }
    }

    /// <summary>Checks descendants without traversing any reparse point.</summary>
    public static void EnsureTreeHasNoReparsePoints(string root)
    {
        EnsureNoReparsePoints(root);
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Managed storage contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static bool IsSameOrWithin(string path, string root)
    {
        if (string.Equals(path, root, PathComparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }
}
