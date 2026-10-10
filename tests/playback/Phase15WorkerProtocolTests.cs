using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Server;
using Hispania.Playback.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

[Trait("Category", "Phase15Compatibility")]
public sealed class Phase15WorkerProtocolTests
{
    [Fact]
    public async Task HistoricalWorkerConnectUsesCurrentHttpParserAndRejectsUnsupportedMalformedOrTruncatedRequests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Hispania.sln"))) root = root.Parent;
        var text = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "compatibility", "protocols", "0.7", "worker-connect.json"));
        var historical = JsonSerializer.Deserialize<DistributedWorkerConnectRequestV1>(text)!;
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=TEST-ONLY-baseline07", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var coordinator = new DistributedWorkCoordinator();
        coordinator.AuthorizeWorker("test-worker-07", historical.Inventory.Capabilities, Convert.ToHexString(SHA256.HashData(certificate.RawData)));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDistributedWorkCoordinatorV1>(coordinator);
        var transferDirectory = Path.Combine(Path.GetTempPath(), "hispania-compat-transfer-" + Guid.NewGuid().ToString("N"));
        using var transfers = new DistributedMediaTransferRegistry(transferDirectory);
        builder.Services.AddSingleton<IDistributedMediaTransferBrokerV1>(transfers);
        await using var application = builder.Build();
        application.Use(async (context, next) => { context.Connection.ClientCertificate = certificate; await next(context); });
        application.MapDistributedWorkerEndpoints(); await application.StartAsync();
        using var client = application.GetTestClient(); client.BaseAddress = new Uri("https://localhost");
        async Task<HttpResponseMessage> Send(string json) => await client.PostAsync("/internal/worker/v1/test-worker-07/connect", new StringContent(json, Encoding.UTF8, "application/json"));
        using var valid = await Send(text);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var response = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal(1, response.RootElement.GetProperty("selectedProtocolVersion").GetInt32());
        using var unsupported = await Send(JsonSerializer.Serialize(historical with { MinimumProtocolVersion = 99, MaximumProtocolVersion = 99 }));
        Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
        using var malformed = await Send("{"); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var truncated = await Send(text[..^3]); Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);
        using var incompatible = await Send("{\"minimumProtocolVersion\":\"future\"}"); Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
        using var unknown = await Send(text.TrimEnd()[..^1] + ",\"unknownFutureField\":true}"); Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }
}
