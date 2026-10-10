using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hispania.Hosting;
using Hispania.Web.Tests;
using Xunit;

namespace Hispania.Deployment.ProcessTests;

[Trait("Category", "Phase20WebDistribution")]
public sealed class WebContributionDistributionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactPhase19ArtifactUpgradeRevalidatesSchemaWithoutChangingPackageState(bool future)
    {
        var predecessor = Environment.GetEnvironmentVariable("HISPANIA_PHASE20_PREDECESSOR") ?? throw new InvalidOperationException("Exact Phase19 artifact is mandatory.");
        Assert.Equal("da57b39149b5d297f3970c17899a4da9eb366eb7", ArtifactManifest.Verify(predecessor).GitSha);
        using var host = new ArtifactProcessTests.HostFixture(predecessor);
        using (var initial = host.Start()) { await host.ReadyAsync(initial); await host.StopAsync(initial); }
        using var package = new WebPackageFixture();
        var page = WebPackageFixture.Document().Contributions[0] with { Permission = "platform.observability.read" };
        var document = new Hispania.ModuleSystem.Contracts.WebContributionDocument(future ? 2 : 1, [page]);
        var archive = package.Archive(document: document, required: false);
        var root = Path.Combine(host.Home, "state", "extensions");
        var registry = new Hispania.Runtime.ModuleRegistry();
        using (var seeded = new Hispania.Runtime.ModuleExtensionCatalog(root, new Version(1, 0, 0), new Hispania.Runtime.ModuleRuntime(registry), registry,
            new Dictionary<string, Func<Hispania.ModuleSystem.Contracts.IModule>>(), [WebPackageFixture.Id], trustedPublisherKeys: package.Keys, requirePublisherSignature: true))
        {
            var plan = await seeded.PlanPackageSetAsync([archive], new Dictionary<string, string> { [WebPackageFixture.Id] = "[1.0.0]" });
            await seeded.ApplyPackageSetAsync(plan, new HashSet<string> { WebPackageFixture.Id }); await seeded.StopManagedPackagesAsync();
        }
        host.WriteConfiguration(new { trustedExtensionIds = new[] { WebPackageFixture.Id }, trustedPublisherKeys = package.Keys });
        var portablePath = Path.Combine(host.Home, "state", "portable.json");
        var portable = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(portablePath), Json)!;
        portable["Modules:TrustedExtensionIds:0"] = WebPackageFixture.Id;
        portable[$"Modules:TrustedPublisherKeys:{WebPackageFixture.Publisher}"] = package.Keys[WebPackageFixture.Publisher];
        portable["Modules:RequirePublisherSignature"] = "true";
        File.WriteAllText(portablePath, JsonSerializer.Serialize(portable, Json));
        async Task AssertHealthyAsync()
        {
            var health = await host.Client.GetFromJsonAsync<JsonElement>("/internal/modules/health");
            Assert.Contains(health.EnumerateArray(), row => row.GetProperty("descriptor").GetProperty("id").GetString() == WebPackageFixture.Id && row.GetProperty("isHealthy").GetBoolean());
        }
        using (var old = host.Start()) { await host.ReadyAsync(old); await AssertHealthyAsync(); await host.StopAsync(old); }
        var state = File.ReadAllBytes(Path.Combine(root, ".package-state", "catalog.json"));
        var bundle = await InstallationOperations.UpgradeAsync(ArtifactProcessTests.Artifact, host.Home);
        Assert.True(File.Exists(Path.Combine(bundle, "extensions", ".package-state", "catalog.json")));
        var password = "TESTONLY!" + Guid.NewGuid().ToString("N");
        host.WriteConfiguration(new { trustedExtensionIds = new[] { WebPackageFixture.Id }, trustedPublisherKeys = package.Keys,
            bootstrapSecret = "env:WEB_BOOTSTRAP", bootstrapPasswordSecret = "env:WEB_PASSWORD", bootstrapUsername = "web-operator" });
        var bootstrap = await host.CommandAsync(["--home", host.Home, "--bootstrap"], new() { ["HISPANIA_SECRET_WEB_BOOTSTRAP"] = Guid.NewGuid().ToString("N"), ["HISPANIA_SECRET_WEB_PASSWORD"] = password });
        Assert.Equal(0, bootstrap.Code);
        host.WriteConfiguration(new { trustedExtensionIds = new[] { WebPackageFixture.Id }, trustedPublisherKeys = package.Keys });
        using (var current = host.Start())
        {
            await host.ReadyAsync(current);
            using var login = await host.Client.PostAsJsonAsync("/api/identity/login", new { username = "web-operator", password });
            login.EnsureSuccessStatusCode(); var session = await login.Content.ReadFromJsonAsync<JsonElement>();
            host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Session", session.GetProperty("sessionToken").GetString());
            await AssertHealthyAsync();
            var projection = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/web/contributions");
            Assert.Equal(future ? 0 : 1, projection.GetProperty("contributions").GetArrayLength());
            if (!future) Assert.Equal("Version One", projection.GetProperty("contributions")[0].GetProperty("page").GetProperty("title").GetString());
            using var shell = await host.Client.GetAsync($"/extensions/{WebPackageFixture.Id}/dashboard"); shell.EnsureSuccessStatusCode();
            await host.StopAsync(current);
        }
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(root, ".package-state", "catalog.json")));
    }
}
