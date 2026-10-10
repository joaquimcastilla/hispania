using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class Phase21MtlsAbuseTests
{
    [Fact]
    public async Task RealTlsRejectsMissingWrongExpiredFuturePeersAndRotatesExactPin()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=TEST ONLY Phase21 CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var server = Issue(ca, "server", -1, 60, true);
        using var a = Issue(ca, "worker-a", -1, 60, false);
        using var b = Issue(ca, "worker-b", -1, 60, false);
        using var expired = Issue(ca, "expired", -30, -1, false);
        using var future = Issue(ca, "future", 1, 30, false);
        var coordinator = new DistributedWorkCoordinator();
        var capability = new DistributedCapabilityV1("test.operation", "1.0");
        coordinator.AuthorizeWorker("node", [capability], a.GetCertHashString(HashAlgorithmName.SHA256));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = server;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.ClientCertificateValidation = (certificate, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
        })));
        builder.Services.AddSingleton<IDistributedWorkCoordinatorV1>(coordinator);
        using var transfers = new DistributedMediaTransferRegistry(Path.Combine(Path.GetTempPath(), "phase21-mtls-" + Guid.NewGuid().ToString("N")));
        builder.Services.AddSingleton<IDistributedMediaTransferBrokerV1>(transfers);
        await using var app = builder.Build(); app.MapDistributedWorkerEndpoints(); await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var inventory = new DistributedWorkerInventoryV1("Linux", "x64", 1, 1024, 1, [], [capability]);
        async Task<HttpStatusCode?> Connect(X509Certificate2? certificate, string node = "node")
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer?.GetCertHashString(HashAlgorithmName.SHA256) == server.GetCertHashString(HashAlgorithmName.SHA256) };
            if (certificate is not null) handler.ClientCertificates.Add(certificate);
            using var client = new HttpClient(handler);
            try
            {
                using var response = await client.PostAsJsonAsync(address + "/internal/worker/v1/" + node + "/connect", new DistributedWorkerConnectRequestV1(1, 1, inventory), timeout.Token);
                return response.StatusCode;
            }
            catch (HttpRequestException exception) when (certificate == expired || certificate == future) { _ = exception; return null; } // TLS handshake rejection also denies access.
        }
        Assert.Equal(HttpStatusCode.OK, await Connect(a));
        Assert.Equal(HttpStatusCode.Unauthorized, await Connect(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Connect(b));
        Assert.Equal(HttpStatusCode.Unauthorized, await Connect(a, "other-node"));
        Assert.NotEqual(HttpStatusCode.OK, await Connect(expired));
        Assert.NotEqual(HttpStatusCode.OK, await Connect(future));
        coordinator.RotateWorkerCertificate("node", b.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.Equal(HttpStatusCode.Unauthorized, await Connect(a));
        Assert.Equal(HttpStatusCode.OK, await Connect(b));
        coordinator.RevokeWorker("node");
        Assert.Equal(HttpStatusCode.Unauthorized, await Connect(b));
        await app.StopAsync(timeout.Token);
    }

    private static X509Certificate2 Issue(X509Certificate2 ca, string name, int startDays, int endDays, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(startDays), DateTimeOffset.UtcNow.AddDays(endDays), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}
