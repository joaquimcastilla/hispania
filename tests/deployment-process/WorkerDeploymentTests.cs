using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Hispania.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Deployment.ProcessTests;

[Trait("Category", "Phase18ProductionDeployment")]
public sealed class WorkerDeploymentTests
{
    [Fact]
    public async Task StandalonePublishedWorkerNegotiatesPinnedMutualTlsAndRestartsFromUnrelatedCwd()
    {
        using var fixture = new ArtifactProcessTests.HostFixture();
        using var serverCertificate = Certificate("coordinator"); using var clientCertificate = Certificate("worker");
        var privatePath = Path.Combine(fixture.Home, "secrets", "worker.pfx");
        var password = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(privatePath, clientCertificate.Export(X509ContentType.Pfx, password)); Protect(privatePath);
        var connected = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(fixture.Port, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate; https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => certificate.Thumbprint == clientCertificate.Thumbprint;
        })));
        await using var coordinator = builder.Build();
        coordinator.MapPost("/internal/worker/v1/node-A/connect", async (HttpRequest request) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            var capabilities = document.RootElement.GetProperty("inventory").GetProperty("capabilities").Clone();
            var attempt = Interlocked.Increment(ref connected);
            Assert.Equal(attempt == 1 ? 0 : 1, capabilities.GetArrayLength());
            return Results.Ok(new { selectedProtocolVersion = 1, authorizedCapabilities = capabilities });
        });
        coordinator.MapPost("/internal/worker/v1/node-A/jobs/lease", () => Results.NoContent());
        coordinator.MapPost("/internal/worker/v1/node-A/heartbeat", () => Results.Ok(new { cancelledJobIds = Array.Empty<string>() }));
        coordinator.MapPost("/internal/worker/v1/node-A/disconnect", () => Results.NoContent());
        await coordinator.StartAsync();
        fixture.WriteConfiguration(new { role = "worker", worker = new { coordinatorUri = $"https://localhost:{fixture.Port}", nodeId = "node-A", certificateFile = "worker.pfx", passwordSecret = "env:WORKER_CERTIFICATE", coordinatorCertificateSha256Pins = new[] { Convert.ToHexString(SHA256.HashData(serverCertificate.RawData)) } } });
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            fixture.WriteConfiguration(new { role = "worker", worker = new { coordinatorUri = $"https://localhost:{fixture.Port}", nodeId = "node-A", certificateFile = "worker.pfx", passwordSecret = "env:WORKER_CERTIFICATE", ffmpegEnabled = attempt == 2, capabilityProviderAssemblies = attempt == 2 ? new[] { "server/Hispania.Transcoding.FFmpeg.dll" } : [], coordinatorCertificateSha256Pins = new[] { Convert.ToHexString(SHA256.HashData(serverCertificate.RawData)) } } });
            using var worker = fixture.Start(environment: new() { ["HISPANIA_SECRET_WORKER_CERTIFICATE"] = password }, worker: true);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref connected) < attempt)
            {
                Assert.False(worker.HasExited, "Published worker must stay active and negotiate mTLS.");
                Assert.True(DateTimeOffset.UtcNow < deadline, "Published worker did not negotiate within 30 seconds."); await Task.Delay(100);
            }
            await fixture.StopAsync(worker);
            var output = await worker.StandardOutput.ReadToEndAsync() + await worker.StandardError.ReadToEndAsync();
            Assert.DoesNotContain(password, output, StringComparison.Ordinal);
            if (attempt == 2) Assert.True(Directory.Exists(Path.Combine(fixture.Home, "state", "cache")));
        }
        await coordinator.StopAsync();
    }

    internal static X509Certificate2 Certificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    internal static void Protect(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); var user = WindowsIdentity.GetCurrent().User!;
            acl.SetOwner(user); acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow)); new FileInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
