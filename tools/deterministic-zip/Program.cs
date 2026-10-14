using System.IO.Compression;
using System.Security.Cryptography;

namespace Hispania.DeterministicZip;

internal static class Program
{
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Hispania.DeterministicZip <source-directory> <output-zip>");
            return 2;
        }

        try
        {
            Create(args[0], args[1]);
            using var stream = File.OpenRead(args[1]);
            Console.WriteLine(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static void Create(string sourceDirectory, string outputZip)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputZip);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Source directory does not exist: {source}");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("The source directory cannot be a symbolic link or reparse point.");
        if (IsWithin(source, output)) throw new InvalidOperationException("The output ZIP must be outside the source directory.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException($"Refusing to overwrite package: {output}");

        var entries = GetEntries(source);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var outputCreated = false;
        try
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            outputCreated = true;
            using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.NoCompression);
                entry.LastWriteTime = FixedTimestamp;
                entry.ExternalAttributes = item.Attributes;
                if (!item.IsDirectory)
                {
                    using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var target = entry.Open();
                    input.CopyTo(target);
                }
            }
        }
        catch
        {
            if (outputCreated) File.Delete(output);
            throw;
        }
    }

    private static List<ArchiveEntry> GetEntries(string root)
    {
        var result = new List<ArchiveEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var fullPath in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"Symbolic links and reparse points are not allowed: {fullPath}");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                ValidatePath(relative);
                if (IsTemporaryPath(relative)) throw new InvalidOperationException($"Temporary or editor-residue path is not allowed in a distribution: {relative}");
                var name = isDirectory ? relative + "/" : relative;
                if (!paths.Add(relative)) throw new InvalidOperationException($"Duplicate or case-colliding archive path: {name}");
                var mode = isDirectory ? 0x41ED : GetFileMode(fullPath);
                result.Add(new ArchiveEntry(fullPath, name, isDirectory, (mode << 16) | (isDirectory ? 0x10 : 0)));
                if (isDirectory) pending.Push(fullPath);
            }
        }

        result.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        return result;
    }

    private static int GetFileMode(string path)
    {
        if (OperatingSystem.IsWindows()) return 0x81A4;
        var mode = File.GetUnixFileMode(path);
        var executable = (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        return executable ? 0x81ED : 0x81A4;
    }

    internal static void ValidatePath(string path)
    {
        var invalidWindowsCharacter = path.Any(character => character is '<' or '>' or '"' or '|' or '?' or '*' or '\\');
        var invalidWindowsSegment = path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' '));
        var reservedWindowsName = path.Split('/').Any(part =>
        {
            var stem = part.Split('.')[0];
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                   (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
        });
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':') || invalidWindowsCharacter || invalidWindowsSegment || reservedWindowsName)
            throw new InvalidOperationException($"Unsafe archive path: {path}");
    }

    private static bool IsTemporaryPath(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (segment is ".DS_Store" or "Thumbs.db" || segment.StartsWith(".package-", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.EndsWith('~') || segment.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || segment.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) || segment.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || segment.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private sealed record ArchiveEntry(string FullPath, string Path, bool IsDirectory, int Attributes);
}
