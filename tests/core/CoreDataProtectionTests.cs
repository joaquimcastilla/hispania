using System.Xml.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class CoreDataProtectionTests
{
    private const string DirectorySetting = "DATA_PROTECTION_KEYS_DIRECTORY";

    [Fact]
    public void MissingSettingPreservesDefaultDataProtectionConfiguration()
    {
        using var root = new TestRoot();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var options = CoreDataProtectionOptions.Read(config, root.Path);

        Assert.Null(options.KeysDirectory);
    }

    [Fact]
    public void RelativeDirectoryIsRejected()
    {
        using var root = new TestRoot();
        var config = Config("relative-keys");

        var exception = Assert.Throws<CoreConfigurationException>(() => CoreDataProtectionOptions.Read(config, PublishPath(root)));

        Assert.DoesNotContain("relative-keys", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptySettingFailsClosedInsteadOfUsingTheDefaultKeyRing()
    {
        using var root = new TestRoot();

        Assert.Throws<CoreConfigurationException>(() => CoreDataProtectionOptions.Read(Config(string.Empty), PublishPath(root)));
    }

    [Fact]
    public void DirectoryInsidePublishRootIsRejected()
    {
        using var root = new TestRoot();
        var publish = Path.Combine(root.Path, "publish");
        var keys = Path.Combine(publish, "keys");
        Directory.CreateDirectory(publish);

        Assert.Throws<CoreConfigurationException>(() => CoreDataProtectionOptions.Read(Config(keys), publish));
        Assert.False(Directory.Exists(keys));
    }

    [Fact]
    public void InaccessibleDirectoryPathIsRejectedWithoutFallback()
    {
        using var root = new TestRoot();
        var parentFile = Path.Combine(root.Path, "not-a-directory");
        File.WriteAllText(parentFile, "test fixture");
        var requested = Path.Combine(parentFile, "keys");

        var exception = Assert.Throws<CoreConfigurationException>(() => CoreDataProtectionOptions.Read(Config(requested), PublishPath(root)));

        Assert.DoesNotContain(requested, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(parentFile));
    }

    [Fact]
    public void DirectoryWithoutCurrentUserWriteAccessIsRejectedAndAclIsRestored()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var keys = Path.Combine(root.Path, "denied-keys");
        Directory.CreateDirectory(keys);
        var originalSecurity = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(keys));
        try
        {
            var currentSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
            var deniedSecurity = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(keys));
            deniedSecurity.AddAccessRule(new FileSystemAccessRule(currentSid, FileSystemRights.CreateFiles | FileSystemRights.WriteData,
                AccessControlType.Deny));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(keys), deniedSecurity);

            Assert.Throws<CoreConfigurationException>(() => CoreDataProtectionOptions.Read(Config(keys), PublishPath(root)));
        }
        finally
        {
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(keys), originalSecurity);
        }
    }

    [Fact]
    public void ValidDirectoryIsCanonicalizedAndProbeIsRemoved()
    {
        using var root = new TestRoot();
        var requested = Path.Combine(root.Path, "isolated", "keys");

        var options = CoreDataProtectionOptions.Read(Config(requested), PublishPath(root));

        Assert.Equal(Path.GetFullPath(requested), options.KeysDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(requested));
    }

    [Fact]
    public void ExplicitDirectoryPersistsDpapiProtectedKeysAndSurvivesHostRebuildWithoutProfileKeyRingFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var data = Path.Combine(root.Path, "core-data");
        var keys = Path.Combine(root.Path, "data-protection-keys");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(keys);
        const string payload = "test-only-dp-protected-payload";

        string protectedValue;
        using (var first = Build(data, keys))
        {
            var capturedLogs = new List<string>();
            first.Services.GetRequiredService<ILoggerFactory>().AddProvider(new CaptureLoggerProvider(capturedLogs));
            var protector = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("CoreDataProtectionTests");
            protectedValue = protector.Protect(payload);
            Assert.DoesNotContain(capturedLogs, message => message.Contains(payload, StringComparison.Ordinal));
            Assert.DoesNotContain(capturedLogs, message => message.Contains(protectedValue, StringComparison.Ordinal));
        }

        var keyFiles = Directory.GetFiles(keys, "*.xml");
        Assert.NotEmpty(keyFiles);
        var keyDocuments = keyFiles.Select(XDocument.Load).ToArray();
        Assert.All(keyDocuments, document =>
        {
            var encryptedSecret = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "encryptedSecret");
            Assert.NotNull(encryptedSecret);
            Assert.Contains("Dpapi", encryptedSecret.Attribute("decryptorType")?.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "secret");
        });

        // ASP.NET Core's default key-ring location is not the configured isolated directory.
        // Checking key IDs avoids relying on concurrent activity in the user's profile.
        var configuredKeyIds = keyDocuments.Select(document => document.Root?.Attribute("id")?.Value)
            .Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(configuredKeyIds);

        using var second = Build(data, keys);
        var restored = second.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("CoreDataProtectionTests").Unprotect(protectedValue);
        Assert.Equal(payload, restored);
        Assert.Equal(keyFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(keys, "*.xml").OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    private static WebApplication Build(string data, string keys) => CoreHost.Build(new Dictionary<string, string?>
    {
        ["DATA_DIRECTORY"] = data,
        [DirectorySetting] = keys
    }, httpEnabled: false);

    private static IConfiguration Config(string keys) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [DirectorySetting] = keys })
        .Build();

    private static string PublishPath(TestRoot root) => Path.Combine(root.Path, "publish");

    private sealed class TestRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-core-dp-tests", Guid.NewGuid().ToString("N"));

        public TestRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class CaptureLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(messages);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }
}
