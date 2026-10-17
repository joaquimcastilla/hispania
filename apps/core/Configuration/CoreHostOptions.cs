using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hispania.Core.Configuration;

internal sealed record CoreHostOptions(IPAddress ListenAddress, int Port, TimeSpan ShutdownTimeout, LogLevel MinimumLogLevel, string Version, string EnvironmentName)
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

        var version = typeof(CoreHostOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return new CoreHostOptions(address, port, TimeSpan.FromSeconds(timeout), minimumLogLevel, version, environmentName);
    }
}

internal sealed class CoreConfigurationException(string message) : Exception(message);
