using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ExtensionDemo;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;

namespace Hispania.Web.Tests;

public sealed class WebPackageFixture : IDisposable
{
    public const string Id = "test.package.web";
    public const string Publisher = "TEST-ONLY-web";
    public const string Permission = "extensions.test.package.web.view";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RSA key = RSA.Create(2048);
    public string Root { get; }
    public string Installed => Path.Combine(Root, "extensions");
    public Dictionary<string, string> Keys { get; }
    public ModuleRegistry Registry { get; } = new();
    public ModuleRuntime Runtime { get; }
    public ModuleExtensionCatalog Catalog { get; }
    public WebPackageFixture(string? root = null, bool hostSupport = true, bool authorized = true)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "hispania-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Keys = new() { [Publisher] = key.ExportSubjectPublicKeyInfoPem() };
        if (hostSupport) Registry.Register(new WebContributionHostModule());
        Runtime = new(Registry);
        if (hostSupport) Runtime.StartModuleAsync("platform.web.contributions", CancellationToken.None).GetAwaiter().GetResult();
        Catalog = new(Installed, new Version(1, 0, 0), Runtime, Registry, new Dictionary<string, Func<IModule>>(),
            authorized ? [Id, "test.package.base"] : [], trustedPublisherKeys: Keys, requirePublisherSignature: true);
    }
    public static WebContributionDocument Document(string title = "Version One") => new(1,
        [new("dashboard", $"/extensions/{Id}/dashboard", title, Permission, new("status", "PrimaryNavigation", 10),
            [new("text", "<script>alert(1)</script>"), new("table", "Estado", "platform.health"), new("button", "Comprobar estado", "platform.health.refresh")])]);

    public static ModuleManifest Manifest(Type? type = null, bool required = true, string version = "1.0.0", PackageDependency[]? dependencies = null)
    {
        type ??= typeof(PackageWebModule);
        return new() { Id = Id, Name = "TEST ONLY Web package", Version = "1.0.0", Implements = [], Provides = [],
            Requires = required ? [WebContributionSchema.Requirement] : [], Permissions = [Permission],
            MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted", EntryAssembly = "Hispania.ExtensionDemo.dll", EntryType = type.FullName,
            SharedAssemblies = ["Hispania.ExtensionDemo.Contracts"], PackageFormatVersion = 2, PackageVersion = version, PackageDependencies = dependencies ?? [] };
    }
    public string Archive(string version = "1.0.0", WebContributionDocument? document = null, bool unhealthy = false, bool required = true, byte[]? assetBytes = null, PackageDependency[]? dependencies = null)
    {
        var payload = new Dictionary<string, byte[]> { ["Hispania.ExtensionDemo.dll"] = File.ReadAllBytes(typeof(PackageWebModule).Assembly.Location),
            ["Hispania.ExtensionDemo.Contracts.dll"] = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location),
            [WebContributionSchema.Path] = JsonSerializer.SerializeToUtf8Bytes(document ?? Document(), Json) };
        foreach (var asset in (document ?? Document()).Contributions.Select(page => page.Navigation.Asset).OfType<string>().Distinct(StringComparer.Ordinal))
            payload[asset] = assetBytes ?? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bJkAAAAASUVORK5CYII=");
        var manifest = Manifest(!required ? typeof(OptionalPackageWebModule) : unhealthy ? typeof(UnhealthyPackageWebModule) : typeof(PackageWebModule), required, version, dependencies) with
        { Files = payload.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)), StringComparer.Ordinal) };
        manifest = ModulePackageSignature.Sign(manifest, Publisher, key.ExportPkcs8PrivateKeyPem());
        var path = Path.Combine(Root, version + ".hispania");
        using var output = File.Create(path); using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        using (var entry = zip.CreateEntry("module.json").Open()) JsonSerializer.Serialize(entry, manifest, Json);
        foreach (var pair in payload) { using var entry = zip.CreateEntry(pair.Key).Open(); entry.Write(pair.Value); }
        return path;
    }
    public string BaseArchive()
    {
        var payload = new Dictionary<string, byte[]> {
            ["Hispania.ExtensionDemo.dll"] = File.ReadAllBytes(typeof(PackageBaseModule).Assembly.Location),
            ["Hispania.ExtensionDemo.Contracts.dll"] = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location) };
        var manifest = Manifest(typeof(PackageBaseModule), false) with { Id = "test.package.base", Name = "TEST ONLY package base",
            Implements = ["test.package.base@1.0"], Provides = ["test.package.base@1.0"], Permissions = [],
            Files = payload.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value))) };
        manifest = ModulePackageSignature.Sign(manifest, Publisher, key.ExportPkcs8PrivateKeyPem());
        var path = Path.Combine(Root, "base.hispania");
        using var output = File.Create(path); using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        using (var entry = zip.CreateEntry("module.json").Open()) JsonSerializer.Serialize(entry, manifest, Json);
        foreach (var pair in payload) { using var entry = zip.CreateEntry(pair.Key).Open(); entry.Write(pair.Value); }
        return path;
    }
    public Task<PackageOperationPlan> Plan(string archive, string version = "1.0.0") => Catalog.PlanPackageSetAsync([archive], new Dictionary<string, string> { [Id] = $"[{version}]" });
    public void Dispose()
    {
        try { Catalog.StopManagedPackagesAsync().GetAwaiter().GetResult(); } finally { Catalog.Dispose(); key.Dispose(); }
    }
}
