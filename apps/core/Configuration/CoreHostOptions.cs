using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace Hispania.Core.Configuration;

internal sealed record CoreHostOptions(IPAddress ListenAddress, int Port, TimeSpan ShutdownTimeout, LogLevel MinimumLogLevel, int LoginRateLimit, int LoginRateWindowSeconds, string Version, string EnvironmentName, CoreStorageOptions Storage, CoreDataProtectionOptions DataProtection, string? HttpsCertificatePath)
{
    public const string DefaultListenAddress = "127.0.0.1";
    public const int DefaultPort = 5070;
    public const int DefaultShutdownTimeoutSeconds = 10;

    public static CoreHostOptions Read(IConfiguration configuration, string environmentName)
    {
        var addressText = configuration["LISTEN_ADDRESS"] ?? DefaultListenAddress;
        if (!IPAddress.TryParse(addressText, out var address) || !IPAddress.IsLoopback(address))
            throw new CoreConfigurationException("LISTEN_ADDRESS must be an IPv4 or IPv6 loopback address.");

        var portText = configuration["PORT"];
        if (portText is not null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort is < 1 or > 65535))
            throw new CoreConfigurationException("PORT must be an integer between 1 and 65535.");
        var port = portText is null ? DefaultPort : int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture);

        var timeoutText = configuration["SHUTDOWN_TIMEOUT_SECONDS"];
        if (timeoutText is not null && (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTimeout) || parsedTimeout is < 1 or > 120))
            throw new CoreConfigurationException("SHUTDOWN_TIMEOUT_SECONDS must be an integer between 1 and 120.");
        var timeout = timeoutText is null ? DefaultShutdownTimeoutSeconds : int.Parse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture);

        var logLevelText = configuration["LOG_LEVEL"] ?? nameof(LogLevel.Information);
        if (!Enum.TryParse<LogLevel>(logLevelText, ignoreCase: true, out var minimumLogLevel) ||
            !Enum.IsDefined(minimumLogLevel) || minimumLogLevel == LogLevel.None)
            throw new CoreConfigurationException("LOG_LEVEL must be Trace, Debug, Information, Warning, Error, or Critical.");

        var loginLimitText = configuration["LOGIN_RATE_LIMIT"];
        if (loginLimitText is not null && (!int.TryParse(loginLimitText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLoginLimit) || parsedLoginLimit is < 1 or > 1000))
            throw new CoreConfigurationException("LOGIN_RATE_LIMIT must be an integer between 1 and 1000.");
        var loginLimit = loginLimitText is null ? 20 : int.Parse(loginLimitText, NumberStyles.None, CultureInfo.InvariantCulture);
        var loginWindowText = configuration["LOGIN_RATE_WINDOW_SECONDS"];
        if (loginWindowText is not null && (!int.TryParse(loginWindowText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLoginWindow) || parsedLoginWindow is < 1 or > 3600))
            throw new CoreConfigurationException("LOGIN_RATE_WINDOW_SECONDS must be an integer between 1 and 3600.");
        var loginWindow = loginWindowText is null ? 60 : int.Parse(loginWindowText, NumberStyles.None, CultureInfo.InvariantCulture);

        var version = typeof(CoreHostOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var storage = CoreStorageOptions.Read(configuration, AppContext.BaseDirectory);
        var dataProtection = CoreDataProtectionOptions.Read(configuration, AppContext.BaseDirectory);
        var certificatePath = configuration["HTTPS_CERTIFICATE_PATH"];
        return new CoreHostOptions(address, port, TimeSpan.FromSeconds(timeout), minimumLogLevel, loginLimit, loginWindow, version, environmentName, storage, dataProtection, certificatePath);
    }

    public X509Certificate2 LoadHttpsCertificate(string? certificatePassword)
    {
        if (string.IsNullOrWhiteSpace(HttpsCertificatePath))
            throw new CoreConfigurationException("HTTPS_CERTIFICATE_PATH must identify a locally protected TLS certificate.");
        if (!Path.IsPathFullyQualified(HttpsCertificatePath))
            throw new CoreConfigurationException("HTTPS_CERTIFICATE_PATH must be an absolute file path.");
        try
        {
            if (!File.Exists(HttpsCertificatePath) || (File.GetAttributes(HttpsCertificatePath) & FileAttributes.ReparsePoint) != 0)
                throw new CoreConfigurationException("The configured HTTPS certificate must be an existing regular file.");
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(HttpsCertificatePath, certificatePassword,
                X509KeyStorageFlags.UserKeySet);
            var hasServerAuthenticationUsage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == "1.3.6.1.5.5.7.3.1"));
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                certificate.Dispose();
                throw new CoreConfigurationException("The configured HTTPS certificate must have a valid private key and be within its validity period.");
            }
            if (!hasServerAuthenticationUsage)
            {
                certificate.Dispose();
                throw new CoreConfigurationException("The configured HTTPS certificate must be valid for TLS server authentication.");
            }
            return certificate;
        }
        catch (CoreConfigurationException) { throw; }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new CoreConfigurationException("The configured HTTPS certificate could not be loaded.");
        }
    }
}

internal sealed class CoreConfigurationException(string message) : Exception(message);
