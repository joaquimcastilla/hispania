using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Server;
using Hispania.Web.Tests;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase20WebBackupTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealOfflineBackupRestoresUiAndRevalidatesOptionalFutureSchema(bool future)
    {
        using var installation = new Phase17BackupRestoreTests.Installation();
        using var fixture = new WebPackageFixture(installation.Source);
        var document = WebPackageFixture.Document();
        if (future) document = document with { SchemaGeneration = 2 };
        var archive = fixture.Archive(document: document, required: !future);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(archive), new HashSet<string> { WebPackageFixture.Id });
        await fixture.Catalog.StopManagedPackagesAsync();
        File.WriteAllText(Path.Combine(installation.Source, "portable.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        { ["Persistence:Provider"] = "sqlite", ["Modules:TrustedPublisherKeys:" + WebPackageFixture.Publisher] = fixture.Keys[WebPackageFixture.Publisher],
            ["Modules:TrustedExtensionIds:0"] = WebPackageFixture.Id, ["Modules:RequirePublisherSignature"] = "true" }));
        await BackupInstallation.CreateAsync(installation.Source, installation.Bundle, default);
        Directory.Delete(installation.Source, true);
        await BackupInstallation.RestoreAsync(installation.Bundle, installation.Target, default);
        var registry = new ModuleRegistry(); registry.Register(new WebContributionHostModule()); var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync("platform.web.contributions", default);
        using var restored = new ModuleExtensionCatalog(Path.Combine(installation.Target, "extensions"), new Version(1, 0, 0), runtime, registry,
            new Dictionary<string, Func<IModule>>(), [WebPackageFixture.Id], trustedPublisherKeys: fixture.Keys, requirePublisherSignature: true);
        try
        {
            await restored.RecoverPackageSetAsync();
            var snapshot = await restored.InspectWebContributionsAsync();
            Assert.True(runtime.GetHealth(WebPackageFixture.Id).IsHealthy);
            if (future) { Assert.Empty(snapshot.Contributions); Assert.Equal("Invalid", Assert.Single(snapshot.Diagnostics).State); }
            else Assert.Equal("Version One", Assert.Single(snapshot.Contributions).Page.Title);
        }
        finally { await restored.StopManagedPackagesAsync(); }
    }
}
