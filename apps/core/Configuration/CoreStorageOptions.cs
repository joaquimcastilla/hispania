using Microsoft.Extensions.Configuration;
using Hispania.Runtime;

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
        try
        {
            canonicalDirectory = StoragePathGuard.RequireOutside(dataDirectory, applicationBaseDirectory,
                "DATA_DIRECTORY cannot overlap the application publish directory.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new CoreConfigurationException("DATA_DIRECTORY is invalid.");
        }

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

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
