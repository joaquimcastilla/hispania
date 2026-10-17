using Microsoft.Extensions.Configuration;

namespace Hispania.Core.Configuration;

internal sealed record CoreStorageOptions(string DataDirectory, string DatabasePath)
{
    public const string DatabaseFileName = "core.db";

    public static CoreStorageOptions Read(IConfiguration configuration, string applicationBaseDirectory)
    {
        var configured = configuration["DATA_DIRECTORY"];
        var dataDirectory = string.IsNullOrWhiteSpace(configured) ? GetDefaultDataDirectory() : configured.Trim();
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new CoreConfigurationException("DATA_DIRECTORY must be an absolute path.");

        string canonicalDirectory;
        string canonicalApplicationDirectory;
        try
        {
            canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
            canonicalApplicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationBaseDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CoreConfigurationException("DATA_DIRECTORY is invalid.");
        }

        if (IsSameOrWithin(canonicalDirectory, canonicalApplicationDirectory))
            throw new CoreConfigurationException("DATA_DIRECTORY cannot be inside the application publish directory.");

        var databasePath = Path.GetFullPath(Path.Combine(canonicalDirectory, DatabaseFileName));
        if (!IsWithin(databasePath, canonicalDirectory))
            throw new CoreConfigurationException("The Core database path must remain inside DATA_DIRECTORY.");

        return new CoreStorageOptions(canonicalDirectory, databasePath);
    }

    private static string GetDefaultDataDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new CoreConfigurationException("No per-user local application data directory is available; configure DATA_DIRECTORY.");

        return Path.Combine(localData, "Hispania", "Core");
    }

    private static bool IsSameOrWithin(string path, string root)
    {
        if (string.Equals(path, root, PathComparison)) return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
