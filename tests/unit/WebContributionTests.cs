using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Web.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Hispania.Runtime.Tests;

public sealed class WebContributionTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData("future")][InlineData("component")][InlineData("route")][InlineData("collision")]
    [InlineData("javascript")][InlineData("data")][InlineData("file")][InlineData("permission")]
    [InlineData("traversal")][InlineData("encoded")][InlineData("absolute")][InlineData("oversized")]
    [InlineData("slot")][InlineData("count")][InlineData("duplicate")][InlineData("malformed")]
    public void BoundedSchemaRejectsUnsafeDescriptors(string scenario)
    {
        using var fixture = new WebPackageFixture();
        var document = WebPackageFixture.Document(); var page = document.Contributions[0];
        document = scenario switch
        {
            "future" => document with { SchemaGeneration = 2 },
            "component" => document with { Contributions = [page with { Components = [new("iframe", "bad")] }] },
            "route" => document with { Contributions = [page with { Route = "/admin" }] },
            "collision" => document with { Contributions = [page, page] },
            "javascript" or "data" or "file" => document with { Contributions = [page with { Components = [new("button", "bad", scenario + ":alert(1)")] }] },
            "permission" => document with { Contributions = [page with { Permission = "platform.admin" }] },
            "traversal" => document with { Contributions = [page with { Navigation = page.Navigation with { Asset = "web/assets/../bad.png" } }] },
            "encoded" => document with { Contributions = [page with { Navigation = page.Navigation with { Asset = "web/assets/%2e%2e.png" } }] },
            "absolute" => document with { Contributions = [page with { Navigation = page.Navigation with { Asset = "C:/bad.png" } }] },
            "slot" => document with { Contributions = [page with { Navigation = page.Navigation with { Section = "Login" } }] },
            "count" => document with { Contributions = Enumerable.Repeat(page, 33).ToArray() },
            _ => document
        };
        var bytes = scenario == "malformed" ? "{"u8.ToArray() : scenario == "duplicate" ? "{\"schemaGeneration\":1,\"schemaGeneration\":1}"u8.ToArray() :
            scenario == "oversized" ? new byte[WebContributionValidator.MaximumBytes + 1] : JsonSerializer.SerializeToUtf8Bytes(document, Json);
        Assert.ThrowsAny<Exception>(() => WebContributionValidator.Parse(WebPackageFixture.Manifest(), bytes));
    }

    [Fact]
    public void MaliciousDisplayStringsRemainLiteralAndFutureFieldsAreNotInterpreted()
    {
        using var fixture = new WebPackageFixture();
        var document = WebPackageFixture.Document("<img onerror=alert(1)>");
        var parsed = WebContributionValidator.Parse(WebPackageFixture.Manifest(), JsonSerializer.SerializeToUtf8Bytes(document, Json));
        Assert.Equal("<img onerror=alert(1)>", parsed.Contributions[0].Title);
        Assert.Equal("<script>alert(1)</script>", parsed.Contributions[0].Components[0].Text);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaGeneration = 1, script = "alert(1)", contributions = document.Contributions }, Json);
        Assert.Throws<JsonException>(() => WebContributionValidator.Parse(WebPackageFixture.Manifest(), bytes));
    }

    [Fact]
    public async Task SignedDescriptorTamperingRejectsBeforeInstallAndMissingHostCapabilityCannotActivate()
    {
        using var unsupported = new WebPackageFixture(hostSupport: false);
        var path = unsupported.Archive(); var plan = await unsupported.Plan(path);
        Assert.False(plan.Resolved); Assert.False(Directory.Exists(unsupported.Installed));
        using var fixture = new WebPackageFixture();
        path = fixture.Archive();
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry(WebContributionSchema.Path)!.Delete();
            using var target = zip.CreateEntry(WebContributionSchema.Path).Open(); target.Write("malicious"u8);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Plan(path));
        Assert.False(Directory.Exists(fixture.Installed));
    }

    [Fact]
    public async Task LifecycleAtomicallyPublishesDisableUpdateRollbackUninstallAndRebuildsWithoutUiPersistence()
    {
        using var fixture = new WebPackageFixture();
        var path = fixture.Archive(); var plan = await fixture.Plan(path);
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        plan = await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>());
        await fixture.Catalog.ApplyPackageSetAsync(plan, new HashSet<string> { WebPackageFixture.Id });
        Assert.Equal("Version One", Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions).Page.Title);
        path = fixture.Archive("2.0.0", WebPackageFixture.Document("Version Two"));
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(path, "2.0.0"));
        Assert.Equal("Version Two", Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions).Page.Title);
        path = fixture.Archive("3.0.0", WebPackageFixture.Document("Broken"), unhealthy: true);
        var failingPlan = await fixture.Plan(path, "3.0.0");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Catalog.ApplyPackageSetAsync(failingPlan));
        Assert.Equal("Version Two", Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions).Page.Title);
        var readTasks = Enumerable.Range(0, 20).Select(_ => fixture.Catalog.InspectWebContributionsAsync()).ToArray();
        plan = await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>());
        await fixture.Catalog.ApplyPackageSetAsync(plan, new HashSet<string>());
        foreach (var snapshot in await Task.WhenAll(readTasks)) Assert.True(snapshot.Contributions.Count is 0 or 1);
        Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>(), new HashSet<string> { WebPackageFixture.Id }));
        Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task UnauthorizedOrRevokedPackagesHaveNoUi(bool revoked)
    {
        using var fixture = new WebPackageFixture(authorized: revoked);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(fixture.Archive()));
        if (revoked)
        {
            await fixture.Catalog.ApplyPackageSetAsync(await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>()), new HashSet<string> { WebPackageFixture.Id });
            await fixture.Catalog.RevokePublisherAsync(WebPackageFixture.Publisher);
        }
        Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        Assert.Equal("Blocked", Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Diagnostics).State);
    }

    [Theory][InlineData("<html><script>alert(1)</script>")][InlineData("not a PNG")]
    public void MimeConfusionIsRejected(string content) => Assert.Throws<InvalidDataException>(() => WebContributionValidator.ValidatePng(System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task PngAssetsUseAuthenticatedHashAndDisappearOnDisableWithTraversalAndMimeNegatives()
    {
        using var fixture = new WebPackageFixture();
        var page = WebPackageFixture.Document().Contributions[0];
        var document = new WebContributionDocument(1, [page with { Navigation = page.Navigation with { Asset = "web/assets/icon.png" } }]);
        var path = fixture.Archive(document: document);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(path), new HashSet<string> { WebPackageFixture.Id });
        var item = Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        Assert.NotNull(await fixture.Catalog.ReadWebAssetAsync(item.PackageId, item.ContentHash, "icon.png"));
        foreach (var name in new[] { "../icon.png", "%2e%2e%2ficon.png", "/icon.png", "C:/icon.png", "icon.html", "icon.js", "icon.svg" })
            Assert.Null(await fixture.Catalog.ReadWebAssetAsync(item.PackageId, item.ContentHash, name));
        Assert.Null(await fixture.Catalog.ReadWebAssetAsync(item.PackageId, new string('0', 64), "icon.png"));
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Catalog.PlanPackageSetAsync([], new Dictionary<string, string>()), new HashSet<string>());
        Assert.Null(await fixture.Catalog.ReadWebAssetAsync(item.PackageId, item.ContentHash, "icon.png"));
        path = fixture.Archive("2.0.0", document, assetBytes: "<html><script>alert(1)</script>"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Plan(path, "2.0.0"));
    }

    [Fact]
    public async Task OptionalFutureSchemaDoesNotStopBackendAndCacheIsOnlyAnEphemeralProjection()
    {
        using var fixture = new WebPackageFixture();
        var path = fixture.Archive(document: WebPackageFixture.Document() with { SchemaGeneration = 2 }, required: false);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(path), new HashSet<string> { WebPackageFixture.Id });
        Assert.True(fixture.Runtime.GetHealth(WebPackageFixture.Id).IsHealthy);
        var snapshot = await fixture.Catalog.InspectWebContributionsAsync();
        Assert.Empty(snapshot.Contributions); Assert.Equal("Invalid", Assert.Single(snapshot.Diagnostics).State);
        await fixture.Catalog.StopManagedPackagesAsync();
        var registry = new ModuleRegistry(); registry.Register(new WebContributionHostModule()); var runtime = new ModuleRuntime(registry);
        await runtime.StartModuleAsync("platform.web.contributions", default);
        using var restarted = new ModuleExtensionCatalog(fixture.Installed, new Version(1, 0, 0), runtime, registry,
            new Dictionary<string, Func<Hispania.ModuleSystem.Contracts.IModule>>(), [WebPackageFixture.Id], trustedPublisherKeys: fixture.Keys, requirePublisherSignature: true);
        await restarted.RecoverPackageSetAsync();
        Assert.Equal(snapshot.Diagnostics, (await restarted.InspectWebContributionsAsync()).Diagnostics);
        await restarted.StopManagedPackagesAsync();
    }

    [Fact]
    public async Task RegistryMeasuresDozensOfContributionsWithoutAnArbitraryLatencySla()
    {
        using var fixture = new WebPackageFixture();
        var page = WebPackageFixture.Document().Contributions[0];
        var document = new WebContributionDocument(1, Enumerable.Range(0, 32).Select(index => page with
        { Id = "page" + index, Route = $"/extensions/{WebPackageFixture.Id}/page{index}" }).ToArray());
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(fixture.Archive(document: document)), new HashSet<string> { WebPackageFixture.Id });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 20; index++) Assert.Equal(32, (await fixture.Catalog.InspectWebContributionsAsync()).Contributions.Count);
        output.WriteLine($"Phase20 registry: 32 contributions, 20 coherent reads, {stopwatch.Elapsed.TotalMilliseconds:F1} ms; parsed descriptor cache is ephemeral; no latency SLA.");
    }
    [Fact]
    public async Task SignedInventoryCannotBeRewrittenToAuthenticateNewWebBytes()
    {
        using var fixture = new WebPackageFixture();
        var path = fixture.Archive();
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            ModuleManifest manifest;
            using (var stream = zip.GetEntry("module.json")!.Open()) manifest = JsonSerializer.Deserialize<ModuleManifest>(stream, Json)!;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(WebPackageFixture.Document("Forged"), Json);
            manifest.Files[WebContributionSchema.Path] = Convert.ToHexString(SHA256.HashData(bytes));
            zip.GetEntry(WebContributionSchema.Path)!.Delete();
            using (var stream = zip.CreateEntry(WebContributionSchema.Path).Open()) stream.Write(bytes);
            zip.GetEntry("module.json")!.Delete();
            using var target = zip.CreateEntry("module.json").Open(); JsonSerializer.Serialize(target, manifest, Json);
        }
        Assert.False((await fixture.Plan(path)).Resolved);
        Assert.False(Directory.Exists(fixture.Installed));
    }

    [Fact]
    public async Task RequiredSignedAssetsEnforceByteBudgetBeforeCommit()
    {
        using var fixture = new WebPackageFixture();
        var page = WebPackageFixture.Document().Contributions[0];
        var document = new WebContributionDocument(1, [page with { Navigation = page.Navigation with { Asset = "web/assets/icon.png" } }]);
        var path = fixture.Archive(document: document, assetBytes: new byte[1024 * 1024 + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Plan(path));
        Assert.False(Directory.Exists(fixture.Installed));
    }

    [Fact]
    public async Task DependencyHealthGatesWebAndBackendThroughTheSamePackageGraph()
    {
        using var fixture = new WebPackageFixture();
        var web = fixture.Archive(dependencies: [new("test.package.base", "[1.0.0]")]);
        var plan = await fixture.Catalog.PlanPackageSetAsync([fixture.BaseArchive(), web], new Dictionary<string, string> { [WebPackageFixture.Id] = "[1.0.0]" });
        Assert.True(plan.Resolved);
        await fixture.Catalog.ApplyPackageSetAsync(plan, new HashSet<string> { WebPackageFixture.Id, "test.package.base" });
        Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        await fixture.Runtime.MarkDegradedAsync("test.package.base", "TEST ONLY dependency failure");
        Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        Assert.NotEqual("Resolved", fixture.Catalog.InspectPackageGraph().Single(row => row.PackageId == WebPackageFixture.Id).Status);
    }

    [Theory][InlineData("valid.json", true)][InlineData("future.json", false)][InlineData("reserved-route.json", false)][InlineData("script.json", false)]
    public void SeparateSchemaOneCorpusHasFrozenBytesAndExplicitReaderOutcomes(string name, bool accepted)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "web-schema1");
        var bytes = File.ReadAllBytes(Path.Combine(root, name));
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Path.Combine(root, "sha256.json")))!;
        Assert.Equal(hashes[name], Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        if (accepted) Assert.Single(WebContributionValidator.Parse(WebPackageFixture.Manifest(), bytes).Contributions);
        else Assert.ThrowsAny<Exception>(() => WebContributionValidator.Parse(WebPackageFixture.Manifest(), bytes));
    }

    [Fact]
    public async Task LinkedAssetDirectoryIsRejectedWithoutServingOutsidePackageBytes()
    {
        using var fixture = new WebPackageFixture();
        var page = WebPackageFixture.Document().Contributions[0];
        var document = new WebContributionDocument(1, [page with { Navigation = page.Navigation with { Asset = "web/assets/icon.png" } }]);
        await fixture.Catalog.ApplyPackageSetAsync(await fixture.Plan(fixture.Archive(document: document)), new HashSet<string> { WebPackageFixture.Id });
        var item = Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        var directory = Path.GetDirectoryName(Directory.GetFiles(fixture.Installed, "icon.png", SearchOption.AllDirectories).Single())!;
        var target = Path.Combine(fixture.Root, "asset-link-target");
        Directory.Move(directory, target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/c", "mklink", "/J", directory, target }) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(directory, target);
            Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
            Assert.Null(await fixture.Catalog.ReadWebAssetAsync(item.PackageId, item.ContentHash, "icon.png"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory); // Only the fixture-owned link, never its target.
            Directory.Move(target, directory);
        }
    }

    [Fact]
    public async Task ConcurrentSinglePackagePoliciesPreserveEachOthersAuthoritativeEnableState()
    {
        using var fixture = new WebPackageFixture();
        var plan = await fixture.Catalog.PlanPackageSetAsync([fixture.Archive(), fixture.BaseArchive()],
            new Dictionary<string, string> { [WebPackageFixture.Id] = "[1.0.0]", ["test.package.base"] = "[1.0.0]" });
        await fixture.Catalog.ApplyPackageSetAsync(plan);
        await Task.WhenAll(fixture.Catalog.SetPackageEnabledAsync(WebPackageFixture.Id, true), fixture.Catalog.SetPackageEnabledAsync("test.package.base", true));
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.True(row.Enabled && row.Healthy));
        Assert.Single((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
        await Task.WhenAll(fixture.Catalog.SetPackageEnabledAsync(WebPackageFixture.Id, false), fixture.Catalog.SetPackageEnabledAsync("test.package.base", false));
        Assert.All(fixture.Catalog.InspectPackageGraph(), row => Assert.False(row.Enabled));
        Assert.Empty((await fixture.Catalog.InspectWebContributionsAsync()).Contributions);
    }

}
