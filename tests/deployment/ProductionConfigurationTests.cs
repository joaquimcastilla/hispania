using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Deployment.Tests;

[Trait("Category", "Phase18ProductionDeployment")]
public sealed class ProductionConfigurationTests
{
    [Fact]
    public void WindowsServiceNameOverrideIsOptionalAndRestrictedToScmSafeCharacters()
    {
        using var fixture = new Fixture("{\"windowsServiceName\":\"Hispania-Test-0123456789abcdef0123456789abcdef-Server\"}");
        Assert.Equal("Hispania-Test-0123456789abcdef0123456789abcdef-Server", fixture.Load().Configuration.WindowsServiceName);
        File.WriteAllText(Path.Combine(fixture.Home, "config", "production.json"), "{\"windowsServiceName\":\"Hispania/Test\"}");
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => fixture.Load()).ExitCode);
    }

    [Fact]
    public void DefaultsAreSafeAndSeparateFromLegacyConfiguration()
    {
        using var fixture = new Fixture("{}");
        var installation = fixture.Load();
        Assert.Equal("http://127.0.0.1:5080", installation.Configuration.Endpoint);
        Assert.Null(installation.Configuration.BootstrapSecret);
        Assert.Empty(installation.Configuration.TrustedExtensionIds);
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Testing:ShutdownToken"] = "legacy", ["Modules:AdminToken"] = "legacy" });
        installation.Apply(config);
        Assert.Null(config["Testing:ShutdownToken"]);
        Assert.Null(config["Modules:AdminToken"]);
        Assert.True(config.GetValue<bool>("Modules:RequirePublisherSignature"));
        Assert.Equal(Path.Combine(fixture.Home, "state"), config["Backup:ManagedRoot"]);
        Assert.StartsWith(AppContext.BaseDirectory, config["Modules:HostPath"]!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"generation\":2}")]
    [InlineData("{\"storag\":{\"root\":\"x\"}}")]
    [InlineData("{\"worker\":{\"pollMillisecons\":500}}")]
    [InlineData("{\"worker\":{\"maximumConcurrentJobs\":0}}")]
    [InlineData("{\"worker\":{\"heartbeatSeconds\":60}}")]
    [InlineData("{\"postgreSql\":{\"port\":70000}}")]
    [InlineData("{\"endpoint\":\"http://0.0.0.0:5080\"}")]
    [InlineData("{\"endpoint\":\"https://localhost:5080\"}")]
    [InlineData("{\"webBuild\":\"typo\"}")]
    [InlineData("{\"logLevel\":\"Trace\"}")]
    [InlineData("{\"libraryRoots\":[\"relative\"]}")]
    [InlineData("{\"tls\":{\"certificateFile\":\"../private.pfx\"}}")]
    [InlineData("{\"bootstrapSecret\":\"plaintext-token\"}")]
    [InlineData("{\"persistence\":\"postgresql\"}")]
    [InlineData("{\"worker\":{\"maximumConcurrentJobs\":\"2\"}}")]
    [InlineData("{\"worker\":null}")]
    [InlineData("{\"worker\":{\"coordinatorCertificateSha256Pins\":null}}")]
    [InlineData("{\"libraryRoots\":[null]}")]
    public void InvalidOrUnknownConfigurationFailsSafely(string json)
    {
        using var fixture = new Fixture(json);
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => fixture.Load()).ExitCode);
    }

    [Fact]
    public void ExplicitOverridesBeatEnvironmentAndFileAndDefaults()
    {
        using var fixture = new Fixture("{\"endpoint\":\"http://127.0.0.1:5101\"}");
        Assert.Equal("http://127.0.0.1:5101", fixture.Load().Configuration.Endpoint);
        using var env = new EnvironmentValue("HISPANIA__ENDPOINT", "http://127.0.0.1:5102");
        Assert.Equal("http://127.0.0.1:5102", fixture.Load().Configuration.Endpoint);
        Assert.Equal("http://127.0.0.1:5103", fixture.Load("--set=endpoint=http://127.0.0.1:5103").Configuration.Endpoint);
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => fixture.Load("--set=endpoint=invalid-high-priority")).ExitCode);
        Environment.SetEnvironmentVariable("HISPANIA__ENDPOINT", "invalid-environment");
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => fixture.Load()).ExitCode);
        Environment.SetEnvironmentVariable("HISPANIA__ENDPOINT", "http://127.0.0.1:5102");
        using var typo = new EnvironmentValue("HISPANIA__WORKER__MAXIMUMCONCURRENTJOBS", "5");
        Assert.Equal(5, fixture.Load().Configuration.Worker.MaximumConcurrentJobs);
    }

    [Fact]
    public void RootIsAbsoluteAndExplicitRootWinsWithoutDuplicateAmbiguity()
    {
        using var fixture = new Fixture("{}");
        using var env = new EnvironmentValue("HISPANIA__HOME", Path.Combine(fixture.Home, "different"));
        Assert.Equal(fixture.Home, fixture.Load().Home);
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => ProductionInstallation.Load(["--home=relative"], "server")).ExitCode);
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => fixture.Load("--home=" + fixture.Home)).ExitCode);
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => fixture.Load("--urls=http://localhost:9999")).ExitCode);
    }

    [Fact]
    public void SecretResolutionDistinguishesMissingAndSupportsRotation()
    {
        using var fixture = new Fixture("{\"bootstrapSecret\":\"env:PHASE18_TEST\"}");
        using var env = new EnvironmentValue("HISPANIA_SECRET_PHASE18_TEST", null);
        var installation = fixture.Load();
        Assert.Equal(3, Assert.Throws<DeploymentException>(() => installation.Secrets.Resolve("env:PHASE18_TEST")).ExitCode);
        Environment.SetEnvironmentVariable("HISPANIA_SECRET_PHASE18_TEST", "credential-A");
        Assert.Equal("credential-A", installation.Secrets.Resolve("env:PHASE18_TEST"));
        Environment.SetEnvironmentVariable("HISPANIA_SECRET_PHASE18_TEST", "credential-B");
        Assert.Equal("credential-B", installation.Secrets.Resolve("env:PHASE18_TEST"));
        Assert.DoesNotContain("credential-", File.ReadAllText(Path.Combine(fixture.Home, "config", "production.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedLocalFileAcceptsOnlyRestrictedPermissions()
    {
        using var fixture = new Fixture("{}");
        var path = Path.Combine(fixture.Home, "secrets", "TEST.secret");
        File.WriteAllText(path, "private-file-value\n");
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
            var current = WindowsIdentity.GetCurrent().User!;
            acl.SetOwner(current); acl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            Assert.Equal("private-file-value", fixture.Load().Secrets.Resolve("file:TEST"));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal("private-file-value", fixture.Load().Secrets.Resolve("file:TEST"));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
        Assert.Equal(3, Assert.Throws<DeploymentException>(() => fixture.Load().Secrets.Resolve("file:TEST")).ExitCode);
    }

    [Fact]
    public void ServiceLogsRedactSecretsAndNeverFormatPrivateExceptionDetails()
    {
        using var fixture = new Fixture("{}");
        var path = Path.Combine(fixture.Home, "logs", "safe.jsonl");
        using var provider = new ProductionLogProvider(path, ["private-value"]);
        var logger = provider.CreateLogger("test");
        using var capture = new StringWriter(); var previous = Console.Out;
        try
        {
            Console.SetOut(capture);
            logger.Log(LogLevel.Error, new EventId(1), "Connection private-value", new InvalidOperationException("private-exception-connection-string"), static (state, exception) => state + exception?.Message);
        }
        finally { Console.SetOut(previous); }
        Assert.DoesNotContain("private-value", capture.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-exception", capture.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", capture.ToString(), StringComparison.Ordinal);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("private-value", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedUpgradeAndHeldMaintenanceOperationBlockStartup()
    {
        using var fixture = new Fixture("{}");
        using (InstallationOperations.Lock(fixture.Home)) Assert.Equal(6, Assert.Throws<DeploymentException>(() => fixture.Load().AcquireRuntimeLease()).ExitCode);
        File.WriteAllText(Path.Combine(fixture.Home, "run", "upgrade-in-progress.json"), "{}");
        Assert.Equal(6, Assert.Throws<DeploymentException>(() => fixture.Load()).ExitCode);
    }

    [Fact]
    public void WorkerRequiresPinnedHttpsAndPrivateMaterial()
    {
        using var fixture = new Fixture("{\"role\":\"worker\"}");
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => ProductionInstallation.Load(["--home", fixture.Home], "worker")).ExitCode);
        File.WriteAllText(Path.Combine(fixture.Home, "config", "production.json"), "{\"role\":\"worker\",\"worker\":{\"nodeId\":\"node-A\",\"coordinatorUri\":\"https://localhost:5443\",\"certificateFile\":\"client.pfx\",\"passwordSecret\":\"env:CERTIFICATE\",\"coordinatorCertificateSha256Pins\":[\"" + new string('a',64) + "\"]}}");
        Assert.Equal("node-A", ProductionInstallation.Load(["--home", fixture.Home], "worker")!.Configuration.Worker.NodeId);
    }

    [Fact]
    public void ImportedPortableConfigurationCannotDisableProductionTrustOrSilentlyChangeOwnerProvider()
    {
        using var fixture = new Fixture("{\"trustedExtensionIds\":[\"operator.allowed\"]}");
        var installation = fixture.Load(); var config = new ConfigurationManager(); installation.Apply(config);
        config["Modules:RequirePublisherSignature"] = "false";
        config["Modules:TrustedExtensionIds:0"] = "legacy.demo";
        config["Modules:TrustedExtensionIds:2"] = "another.legacy.demo";
        config["Modules:TrustedPublisherKeys:legacy"] = "public-key";
        installation.EnforcePublicTrust(config);
        Assert.True(config.GetValue<bool>("Modules:RequirePublisherSignature"));
        Assert.Equal(["operator.allowed"], config.GetSection("Modules:TrustedExtensionIds").Get<string[]>()!);
        Assert.Null(config["Modules:TrustedPublisherKeys:legacy"]);
        config["Persistence:Provider"] = "postgresql";
        Assert.Equal(2, Assert.Throws<DeploymentException>(() => installation.EnforcePublicTrust(config)).ExitCode);
    }

    [Fact]
    public void ArtifactVerifierRejectsCorruptionAddedFilesMissingPayloadsAndFutureMetadata()
    {
        using var fixture = new Fixture("{}"); var artifact = Path.Combine(fixture.Home, "artifact");
        var manifest = new ArtifactManifest { DistributionVersion = "0.7.0", GitSha = new string('a', 40), TargetRid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", BuildUtc = DateTimeOffset.UtcNow };
        foreach (var relative in new[] { "server/Hispania.Server.dll", "server/module-host/Hispania.ModuleHost.dll", "server/ffmpeg-worker/Hispania.FFmpeg.Worker.dll", "server/wwwroot/modern/index.html", "server/wwwroot/classic/index.html", "worker/Hispania.WorkerAgent.dll", "worker/module-host/Hispania.ModuleHost.dll" })
        {
            var path = Path.Combine(artifact, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "metadata-validation-fixture");
            manifest.Files[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        var manifestPath = Path.Combine(artifact, "artifact-manifest.json");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        _ = ArtifactManifest.Verify(artifact);
        manifest.DistributionVersion = "0.7.0-rc.1"; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        Assert.Equal("0.7.0-rc.1", ArtifactManifest.Verify(artifact).DistributionVersion);
        manifest.DistributionVersion = "0.7.0-rc.01"; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        Assert.Equal(5, Assert.Throws<DeploymentException>(() => ArtifactManifest.Verify(artifact)).ExitCode);
        manifest.DistributionVersion = null!; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        Assert.Equal(5, Assert.Throws<DeploymentException>(() => ArtifactManifest.Verify(artifact)).ExitCode);
        manifest.DistributionVersion = "0.7.0"; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        var server = Path.Combine(artifact, "server", "Hispania.Server.dll"); File.AppendAllText(server, "corrupt");
        Assert.Equal(5, Assert.Throws<DeploymentException>(() => ArtifactManifest.Verify(artifact)).ExitCode);
        File.WriteAllText(server, "metadata-validation-fixture");
        var extra = Path.Combine(artifact, "unexpected.secret"); File.WriteAllText(extra, "private-value");
        Assert.Equal(5, Assert.Throws<DeploymentException>(() => ArtifactManifest.Verify(artifact)).ExitCode); File.Delete(extra);
        manifest.ManifestGeneration = 99; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        Assert.Equal(5, Assert.Throws<DeploymentException>(() => ArtifactManifest.Verify(artifact)).ExitCode);
        manifest.ManifestGeneration = 1; File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options)); File.Delete(server);
        Assert.Equal(5, Assert.Throws<DeploymentException>(() => ArtifactManifest.Verify(artifact)).ExitCode);
    }

    private sealed class EnvironmentValue : IDisposable
    {
        private readonly string name; private readonly string? original;
        internal EnvironmentValue(string name, string? value) { this.name = name; original = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        public void Dispose() => Environment.SetEnvironmentVariable(name, original);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Home { get; } = Path.Combine(Path.GetTempPath(), "hispania-phase18 config ñ " + Guid.NewGuid().ToString("N"));
        internal Fixture(string json)
        {
            foreach (var directory in new[] { "config", "secrets", "state", "run", "backups", "logs" }) Directory.CreateDirectory(Path.Combine(Home, directory));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(Home, "secrets"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(Home, "config", "production.json"), json);
        }
        internal ProductionInstallation Load(params string[] arguments) => ProductionInstallation.Load(["--home", Home, ..arguments], "server")!;
        public void Dispose() => Directory.Delete(Home, true);
    }
}
