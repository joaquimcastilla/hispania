using Hispania.Runtime;
using Microsoft.Extensions.Configuration;

namespace Hispania.Core.Configuration;

internal sealed record CoreDataProtectionOptions(string? KeysDirectory)
{
    public static CoreDataProtectionOptions Read(IConfiguration configuration, string applicationBaseDirectory)
    {
        var configured = configuration["DATA_PROTECTION_KEYS_DIRECTORY"];
        if (configured is null) return new CoreDataProtectionOptions((string?)null);
        if (!OperatingSystem.IsWindows())
            throw new CoreConfigurationException("DATA_PROTECTION_KEYS_DIRECTORY is supported only on Windows.");

        try
        {
            var path = configured.Trim();
            if (!Path.IsPathFullyQualified(path))
                throw new CoreConfigurationException("DATA_PROTECTION_KEYS_DIRECTORY must be an absolute path.");

            var canonical = StoragePathGuard.RequireOutside(path, applicationBaseDirectory,
                "DATA_PROTECTION_KEYS_DIRECTORY cannot overlap the application publish directory.");
            Directory.CreateDirectory(canonical);
            StoragePathGuard.EnsureNoReparsePoints(canonical);
            if (!Directory.Exists(canonical))
                throw new IOException("Configured key directory is unavailable.");

            // Fail during configuration, before dependent services are built, if the
            // configured directory cannot create and remove a private probe file.
            var probe = Path.Combine(canonical, $".hispania-dp-check-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }

            StoragePathGuard.EnsureNoReparsePoints(canonical);
            return new CoreDataProtectionOptions(canonical);
        }
        catch (CoreConfigurationException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or
                                          InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new CoreConfigurationException("DATA_PROTECTION_KEYS_DIRECTORY is invalid or inaccessible.");
        }
    }
}
