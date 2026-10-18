using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hispania.CoreArtifactValidator;
using Xunit;

namespace Hispania.CoreArtifactValidator.Tests;

public sealed class ArtifactValidatorTests
{
    [Fact]
    public void CheckedInAllowlistLoadsAndKeepsConditionalAndForbiddenBoundaries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "core-policy.json");
        var policy = JsonSerializer.Deserialize<ArtifactPolicy>(File.ReadAllText(path), ManifestJson.Options);
        Assert.NotNull(policy);
        Assert.Equal(3, policy.SupportedRuntimeIdentifiers.Length);
        Assert.Contains(policy.AllowedDependencies, dependency => dependency.Id == "Microsoft.Data.Sqlite" && dependency.Version == "10.0.12" && dependency.Status == "allowed");
        Assert.Contains(policy.AllowedDependencies, dependency => dependency.Id == "SQLitePCLRaw.lib.e_sqlite3:runtimes/win-x64/native/e_sqlite3.dll" && dependency.Status == "allowed");
        Assert.Contains(policy.AllowedDependencies, dependency => dependency.Id == "SQLitePCLRaw.lib.e_sqlite3:runtimes/linux-arm64/native/libe_sqlite3.so" && dependency.Status == "conditional");
        Assert.Contains(policy.ForbiddenDependencyPrefixes, prefix => prefix == "Microsoft.EntityFrameworkCore");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "e_sqlite3.dll");
        Assert.Contains(policy.AllowedDependencies, dependency => dependency.Id == "NuGet.Versioning" && dependency.Status == "allowed");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "Hispania.Runtime.dll");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "module-host/Hispania.ModuleHost.dll");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "module-host/Hispania.ModuleHost.deps.json");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "module-host/Hispania.ModuleHost.runtimeconfig.json");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "module-host/Hispania.ModuleSystem.Contracts.dll");
        Assert.Contains(policy.AllowedFilePatterns, pattern => pattern == "module-host/Hispania.Platform.Contracts.dll");
        Assert.Contains(policy.ConditionalFilePatterns, pattern => pattern == "NuGet.Common.dll");
        Assert.Contains(policy.AllowedDependencies, dependency => dependency.Id == "infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj" && dependency.Status == "forbidden");
        Assert.Equal(6, policy.ExcludedSha256.Length);
    }

    [Fact]
    public void CheckedInPolicyCreatesAndRevalidatesOnlyASyntheticPublishFixture()
    {
        var parent = Path.Combine(Path.GetTempPath(), "hispania-core-policy-cli-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "artifact");
        Directory.CreateDirectory(root);
        var policyPath = Path.Combine(AppContext.BaseDirectory, "core-policy.json");
        var descriptorPath = Path.Combine(parent, "descriptor.json");
        try
        {
            foreach (var name in new[] { "Hispania.Core.dll", "Hispania.ModuleSystem.Contracts.dll", "Hispania.Platform.Contracts.dll" })
                File.WriteAllBytes(Path.Combine(root, name), [1, 2, 3]);
            File.WriteAllText(Path.Combine(root, "Hispania.Core.deps.json"), """
            {"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"},"targets":{".NETCoreApp,Version=v10.0/win-x64":{"Hispania.Core/1.0.0":{"type":"project","dependencies":{"Hispania.ModuleSystem.Contracts":"1.0.0","Hispania.Platform.Contracts":"1.0.0"}},"Hispania.ModuleSystem.Contracts/1.0.0":{"type":"project"},"Hispania.Platform.Contracts/1.0.0":{"type":"project"}}},"libraries":{"Hispania.Core/1.0.0":{"type":"project"},"Hispania.ModuleSystem.Contracts/1.0.0":{"type":"project"},"Hispania.Platform.Contracts/1.0.0":{"type":"project"}}}
            """);
            File.WriteAllText(Path.Combine(root, "Hispania.Core.runtimeconfig.json"), """
            {"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"},{"name":"Microsoft.AspNetCore.App","version":"10.0.12"}]}}
            """);
            var descriptor = new ArtifactDescriptor(1, "hispania.core", "0123456789abcdef0123456789abcdef01234567", "net10.0", "win-x64", "framework-dependent",
            [
                new("project", "contracts/module-system/Hispania.ModuleSystem.Contracts.csproj", "source", "build", true),
                new("project", "contracts/platform/Hispania.Platform.Contracts.csproj", "source", "build", true)
            ]);
            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, ManifestJson.Options));
            var createArgs = new[] { "create", "--root", root, "--descriptor", descriptorPath, "--policy", policyPath };
            Assert.Equal(0, RunCli(createArgs).ExitCode);
            Assert.Equal(0, RunCli("validate", "--root", root, "--policy", policyPath).ExitCode);

            File.WriteAllBytes(Path.Combine(root, "NuGet.Common.dll"), [4, 5, 6]);
            var conditionalFile = RunCli(createArgs);
            Assert.NotEqual(0, conditionalFile.ExitCode);
            Assert.Contains("File is conditional and not currently authorized: NuGet.Common.dll", conditionalFile.Output, StringComparison.Ordinal);
            File.Delete(Path.Combine(root, "NuGet.Common.dll"));

            var depsPath = Path.Combine(root, "Hispania.Core.deps.json");
            var depsText = File.ReadAllText(depsPath)
                .Replace("\"Hispania.Platform.Contracts\":\"1.0.0\"}", "\"Hispania.Platform.Contracts\":\"1.0.0\",\"Microsoft.NET.Test.Sdk\":\"17.11.1\"}", StringComparison.Ordinal)
                .Replace("\"Hispania.Platform.Contracts/1.0.0\":{\"type\":\"project\"}", "\"Hispania.Platform.Contracts/1.0.0\":{\"type\":\"project\"},\"Microsoft.NET.Test.Sdk/17.11.1\":{\"type\":\"package\"}", StringComparison.Ordinal);
            File.WriteAllText(depsPath, depsText);
            var unlistedPackage = RunCli(createArgs);
            Assert.NotEqual(0, unlistedPackage.ExitCode);
            Assert.Contains("Dependency is not explicitly allowlisted: package Microsoft.NET.Test.Sdk 17.11.1", unlistedPackage.Output, StringComparison.Ordinal);

            depsText = depsText
                .Replace("Microsoft.NET.Test.Sdk", "Microsoft.EntityFrameworkCore.Relational", StringComparison.Ordinal)
                .Replace("17.11.1", "10.0.12", StringComparison.Ordinal);
            File.WriteAllText(depsPath, depsText);
            var forbiddenPackage = RunCli(createArgs);
            Assert.NotEqual(0, forbiddenPackage.ExitCode);
            Assert.Contains("Forbidden dependency: Microsoft.EntityFrameworkCore.Relational", forbiddenPackage.Output, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
    }

    private static (int ExitCode, string Output) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            return (Program.Run(args), output.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    [Fact]
    public void ValidSyntheticManifestPasses()
    {
        using var fixture = new Fixture();
        var report = fixture.Validate();
        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Issues));
    }

    [Fact]
    public void SelectedRidNativeAssetCanBeRootFlattenedButCannotBeMissing()
    {
        using var fixture = new Fixture();
        fixture.AddSqliteNativeAsset();

        var accepted = fixture.Validate();
        Assert.True(accepted.Passed, string.Join(Environment.NewLine, accepted.Issues));

        File.Delete(Path.Combine(fixture.Root, "e_sqlite3.dll"));
        var missing = fixture.Validate();
        Assert.Contains(missing.Issues, issue => issue.Contains("Manifest file is missing: e_sqlite3.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void UnlistedAdditionalFileIsRejected()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "unexpected.txt"), "synthetic");
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Unexpected artifact file", StringComparison.Ordinal));
    }

    [Fact]
    public void RootLevelRightsSensitiveFilePatternIsRejected()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "demo.mp4"), "synthetic marker only");
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Forbidden path: demo.mp4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForbiddenDirectOrTransitivePackageIsRejected(bool direct)
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with
        {
            Dependencies = [.. manifest.Dependencies, new ResolvedDependency("package", "Npgsql", "10.0.0", "publish", direct)]
        });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Forbidden dependency: Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvedTransitivePackageFromDepsGraphIsRejected()
    {
        using var fixture = new Fixture();
        fixture.AddTransitiveNpgsql();
        Assert.Contains(fixture.Manifest.Dependencies, dependency => dependency.Id == "Npgsql" && !dependency.Direct);
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Forbidden dependency: Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void DependencyVersionMustMatchPinnedAllowlist()
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with
        {
            Dependencies = manifest.Dependencies.Select(item => item.Id == "NuGet.Versioning" ? item with { Version = "7.9.1" } : item).ToArray()
        });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Dependency is not explicitly allowlisted: package NuGet.Versioning 7.9.1", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRequiredFileIsRejected()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, "Hispania.Core.deps.json"));
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Manifest file is missing: Hispania.Core.deps.json", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedFileHashIsRejected()
    {
        using var fixture = new Fixture();
        File.AppendAllText(Path.Combine(fixture.Root, "Hispania.Core.dll"), "changed");
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("File size mismatch: Hispania.Core.dll", StringComparison.Ordinal));
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("File SHA-256 mismatch: Hispania.Core.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void IncorrectManifestSizeIsRejected()
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with
        {
            Files = manifest.Files.Select(file => file.Path == "Hispania.Core.dll" ? file with { Size = file.Size + 1 } : file).ToArray()
        });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("File size mismatch: Hispania.Core.dll", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("C:/Users/dev/private.dll")]
    [InlineData("../outside.dll")]
    [InlineData("nested/../../outside.dll")]
    [InlineData("nested\\outside.dll")]
    public void AbsoluteAndTraversalPathsAreRejected(string path)
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with { Files = [manifest.Files[0] with { Path = path }, .. manifest.Files.Skip(1)] });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Unsafe manifest path", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateManifestPathIsRejected()
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with { Files = [.. manifest.Files, manifest.Files[0]] });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Duplicate path", StringComparison.Ordinal));
    }

    [Fact]
    public void CaseInsensitivePathCollisionIsRejected()
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with
        { Files = [.. manifest.Files, manifest.Files[0] with { Path = "hispania.core.dll" }] });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Case-insensitive or portable path collision", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownSchemaVersionIsRejected()
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with { SchemaVersion = 2 });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Unsupported manifest schema version: 2", StringComparison.Ordinal));
    }

    [Fact]
    public void UnapprovedPolicyExceptionIsRejectedFailClosed()
    {
        using var fixture = new Fixture();
        fixture.UpdateManifest(manifest => manifest with
        {
            Exceptions = [new PolicyExceptionRecord("EX-1", "synthetic", "one file", "test evidence", "not approved", DateTimeOffset.UtcNow.AddDays(1))]
        });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("No policy exceptions are authorized", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownUnknownRightsHashIsRejectedWithoutOpeningHistoricalAsset()
    {
        using var fixture = new Fixture();
        const string excludedHash = "FCB341C4F3504B8FA85C75E32F386A3251B5CC31A55385B0B6B45FB4A9AF540E";
        fixture.UpdateManifest(manifest => manifest with
        { Files = [manifest.Files[0] with { Sha256 = excludedHash }, .. manifest.Files.Skip(1)] });
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Excluded rights-sensitive SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedManifestJsonIsRejected()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.ManifestPath, "{not-json");
        Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Manifest could not be read", StringComparison.Ordinal));
    }

    [SymlinkFact]
    public void ExternalSymlinkIsRejectedOrExplicitlySkippedWhenHostCannotCreateOne()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Parent, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "escape.txt"), "synthetic outside target");
        var link = Path.Combine(fixture.Root, "linked-outside");
        if (!SymlinkCapability.TryCreate(link, outside, out var reason))
            throw new InvalidOperationException($"The filesystem capability changed after discovery: {reason}");

        try { Assert.Contains(fixture.Validate().Issues, issue => issue.Contains("Symlink/reparse point rejected", StringComparison.Ordinal)); }
        finally { Directory.Delete(link); }
    }

    private sealed class SymlinkFactAttribute : FactAttribute
    {
        public SymlinkFactAttribute()
        {
            var root = Path.Combine(Path.GetTempPath(), "hispania-symlink-capability-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateDirectory(target);
                if (!SymlinkCapability.TryCreate(link, target, out var reason)) Skip = $"BLOCKED: host cannot create a symlink/junction fixture ({reason}).";
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static class SymlinkCapability
    {
        public static bool TryCreate(string link, string target, out string reason)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("/c");
                    start.ArgumentList.Add("mklink");
                    start.ArgumentList.Add("/J");
                    start.ArgumentList.Add(link);
                    start.ArgumentList.Add(target);
                    using var process = Process.Start(start) ?? throw new IOException("Could not start junction creator.");
                    process.WaitForExit();
                    if (process.ExitCode != 0) { reason = $"mklink /J exited {process.ExitCode}"; return false; }
                }
                else Directory.CreateSymbolicLink(link, target);
                reason = "";
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException or System.ComponentModel.Win32Exception)
            { reason = exception.GetType().Name; return false; }
        }
    }

    [Fact]
    public void ContentIdentityAndSortedFileListAreStableAcrossEnumerationOrderAndGenerationTime()
    {
        using var first = new Fixture(createInReverseOrder: false);
        using var second = new Fixture(createInReverseOrder: true);
        var firstManifest = first.Manifest;
        var secondManifest = second.Manifest;
        Assert.Equal(firstManifest.ContentId, secondManifest.ContentId);
        Assert.Equal(firstManifest.Files.Select(file => file.Path), secondManifest.Files.Select(file => file.Path));
        Assert.NotEqual(firstManifest.GeneratedAtUtc, secondManifest.GeneratedAtUtc);
    }

    [Fact]
    public void CliReturnsNonzeroForPolicyViolation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "unexpected.txt"), "synthetic");
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            var exitCode = Program.Run(["validate", "--root", fixture.Root, "--policy", fixture.PolicyPath, "--manifest", fixture.ManifestPath]);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("Unexpected artifact file", output.ToString(), StringComparison.Ordinal);
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ArtifactPolicy policy;
        public string Parent { get; }
        public string Root { get; }
        public string PolicyPath { get; }
        public string ManifestPath { get; }
        public ArtifactManifest Manifest { get; private set; }

        public Fixture(bool createInReverseOrder = false)
        {
            Parent = Path.Combine(Path.GetTempPath(), "hispania-core-manifest-tests-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Parent, "artifact");
            Directory.CreateDirectory(Root);
            PolicyPath = Path.Combine(Parent, "policy.json");
            ManifestPath = Path.Combine(Root, ArtifactManifestBuilder.ManifestFileName);
            policy = CreatePolicy();
            File.WriteAllText(PolicyPath, JsonSerializer.Serialize(policy, ManifestJson.Options));
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Hispania.Core.dll"] = [1, 3, 3, 7],
                ["Hispania.Core.deps.json"] = DepsJson(),
                ["Hispania.Core.runtimeconfig.json"] = """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"},{"name":"Microsoft.AspNetCore.App","version":"10.0.12"}]}}"""u8.ToArray()
            };
            var ordered = createInReverseOrder ? payloads.Reverse() : payloads;
            foreach (var payload in ordered) File.WriteAllBytes(Path.Combine(Root, payload.Key), payload.Value);
            var descriptor = new ArtifactDescriptor(1, "hispania.core", "0123456789abcdef0123456789abcdef01234567", "net10.0", "win-x64", "framework-dependent",
            [
                new("project", "contracts/module-system/Hispania.ModuleSystem.Contracts.csproj", "source", "build", true),
                new("project", "contracts/platform/Hispania.Platform.Contracts.csproj", "source", "build", true)
            ]);
            Descriptor = descriptor;
            Manifest = Build(createInReverseOrder
                ? DateTimeOffset.Parse("2026-10-17T00:00:00Z", CultureInfo.InvariantCulture) : DateTimeOffset.Parse("2026-10-16T00:00:00Z", CultureInfo.InvariantCulture));
            Save(Manifest);
        }

        private ArtifactDescriptor Descriptor { get; }

        private ArtifactManifest Build(DateTimeOffset generated) => ArtifactManifestBuilder.Create(Root, Descriptor, policy, generated)
            with { PolicyResult = new PolicyResult(policy.PolicyId, "passed", []) };

        public ValidationReport Validate() => ArtifactValidator.Validate(Root, ManifestPath, PolicyPath);
        public void UpdateManifest(Func<ArtifactManifest, ArtifactManifest> update)
        {
            var changed = update(Manifest);
            changed = changed with { ContentId = ManifestJson.ContentId(changed), PolicyResult = new PolicyResult(policy.PolicyId, "passed", []) };
            Save(changed);
        }

        public void AddTransitiveNpgsql()
        {
            var depsPath = Path.Combine(Root, "Hispania.Core.deps.json");
            var deps = File.ReadAllText(depsPath).Replace(
                "\"Hispania.Platform.Contracts/1.0.0\":{\"type\":\"project\"},",
                "\"Hispania.Platform.Contracts/1.0.0\":{\"type\":\"project\",\"dependencies\":{\"Npgsql\":\"10.0.0\"}},\"Npgsql/10.0.0\":{\"type\":\"package\"},",
                StringComparison.Ordinal);
            File.WriteAllText(depsPath, deps);
            Manifest = Build(Manifest.GeneratedAtUtc);
            Save(Manifest);
        }

        public void AddSqliteNativeAsset()
        {
            var depsPath = Path.Combine(Root, "Hispania.Core.deps.json");
            var deps = JsonNode.Parse(File.ReadAllText(depsPath))!.AsObject();
            var targetName = ".NETCoreApp,Version=v10.0/win-x64";
            var target = deps["targets"]![targetName]!.AsObject();
            target["hispania.core/1.0.0"]!["dependencies"]!["Microsoft.Data.Sqlite"] = "10.0.12";
            target["Microsoft.Data.Sqlite/10.0.12"] = JsonNode.Parse("""{"type":"package","dependencies":{"Microsoft.Data.Sqlite.Core":"10.0.12","SQLitePCLRaw.bundle_e_sqlite3":"2.1.12","SQLitePCLRaw.core":"2.1.12"}}""");
            target["Microsoft.Data.Sqlite.Core/10.0.12"] = JsonNode.Parse("""{"type":"package","dependencies":{"SQLitePCLRaw.core":"2.1.12"}}""");
            target["SQLitePCLRaw.bundle_e_sqlite3/2.1.12"] = JsonNode.Parse("""{"type":"package","dependencies":{"SQLitePCLRaw.lib.e_sqlite3":"2.1.12","SQLitePCLRaw.provider.e_sqlite3":"2.1.12"}}""");
            target["SQLitePCLRaw.core/2.1.12"] = JsonNode.Parse("""{"type":"package"}""");
            target["SQLitePCLRaw.lib.e_sqlite3/2.1.12"] = JsonNode.Parse("""{"type":"package","runtimeTargets":{"runtimes/win-x64/native/e_sqlite3.dll":{"rid":"win-x64","assetType":"native"}}}""");
            target["SQLitePCLRaw.provider.e_sqlite3/2.1.12"] = JsonNode.Parse("""{"type":"package","dependencies":{"SQLitePCLRaw.core":"2.1.12"}}""");

            var libraries = deps["libraries"]!.AsObject();
            foreach (var name in new[]
            {
                "Microsoft.Data.Sqlite/10.0.12", "Microsoft.Data.Sqlite.Core/10.0.12", "SQLitePCLRaw.bundle_e_sqlite3/2.1.12",
                "SQLitePCLRaw.core/2.1.12", "SQLitePCLRaw.lib.e_sqlite3/2.1.12", "SQLitePCLRaw.provider.e_sqlite3/2.1.12"
            }) libraries[name] = JsonNode.Parse("""{"type":"package"}""");

            File.WriteAllText(depsPath, deps.ToJsonString());
            foreach (var name in new[] { "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "e_sqlite3.dll" })
                File.WriteAllBytes(Path.Combine(Root, name), [7, 8, 9]);
            Manifest = Build(Manifest.GeneratedAtUtc);
            Save(Manifest);
        }

        private static byte[] DepsJson() => """
        {"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"},"targets":{".NETCoreApp,Version=v10.0/win-x64":{"hispania.core/1.0.0":{"type":"project","dependencies":{"Hispania.ModuleSystem.Contracts":"1.0.0","Hispania.Platform.Contracts":"1.0.0"}},"Hispania.ModuleSystem.Contracts/1.0.0":{"type":"project"},"Hispania.Platform.Contracts/1.0.0":{"type":"project"},"NuGet.Versioning/7.9.0":{"type":"package"}}},"libraries":{"hispania.core/1.0.0":{"type":"project"},"Hispania.ModuleSystem.Contracts/1.0.0":{"type":"project"},"Hispania.Platform.Contracts/1.0.0":{"type":"project"},"NuGet.Versioning/7.9.0":{"type":"package"}}}
        """u8.ToArray();
        private void Save(ArtifactManifest manifest) => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options));
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
        }

        private static ArtifactPolicy CreatePolicy() => new(1, "test-policy-v1", ["net10.0"], ["win-x64"], ["framework-dependent"],
        ["Hispania.Core.dll", "Hispania.Core.deps.json", "Hispania.Core.runtimeconfig.json"],
        [
            new("project", "contracts/module-system/Hispania.ModuleSystem.Contracts.csproj", "source", ["build", "runtime", "publish"], "allowed", "Synthetic fixture contract.", "Hispania.ModuleSystem.Contracts"),
            new("project", "contracts/platform/Hispania.Platform.Contracts.csproj", "source", ["build", "runtime", "publish"], "allowed", "Synthetic fixture contract.", "Hispania.Platform.Contracts"),
            new("framework", "Microsoft.AspNetCore.App", "10.0.*", ["build", "runtime", "publish"], "allowed", "Synthetic framework fixture."),
            new("framework", "Microsoft.NETCore.App", "10.0.*", ["build", "runtime", "publish"], "allowed", "Synthetic framework fixture."),
            new("package", "NuGet.Versioning", "7.9.0", ["build", "runtime", "publish"], "allowed", "Pinned package fixture."),
            new("package", "Microsoft.Data.Sqlite", "10.0.12", ["build", "runtime", "publish"], "allowed", "Pinned SQLite provider fixture."),
            new("package", "Microsoft.Data.Sqlite.Core", "10.0.12", ["build", "runtime", "publish"], "allowed", "Pinned SQLite provider fixture."),
            new("package", "SQLitePCLRaw.bundle_e_sqlite3", "2.1.12", ["build", "runtime", "publish"], "allowed", "Pinned SQLite provider fixture."),
            new("package", "SQLitePCLRaw.core", "2.1.12", ["build", "runtime", "publish"], "allowed", "Pinned SQLite provider fixture."),
            new("package", "SQLitePCLRaw.lib.e_sqlite3", "2.1.12", ["build", "runtime", "publish"], "allowed", "Pinned SQLite provider fixture."),
            new("package", "SQLitePCLRaw.provider.e_sqlite3", "2.1.12", ["build", "runtime", "publish"], "allowed", "Pinned SQLite provider fixture."),
            new("runtimeAsset", "SQLitePCLRaw.lib.e_sqlite3:runtimes/win-x64/native/e_sqlite3.dll", "2.1.12", ["publish"], "allowed", "Selected RID-native fixture.")
        ], ["Npgsql", "Hispania.Media"],
        ["Hispania.Core.dll", "Hispania.Core.deps.json", "Hispania.Core.runtimeconfig.json", "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "e_sqlite3.dll"],
        ["Microsoft.Data.Sqlite.Core.dll", "SQLitePCLRaw.bundle_e_sqlite3.dll", "runtimes/linux-x64/native/libe_sqlite3.so", "runtimes/linux-arm64/native/libe_sqlite3.so"],
        ["FCB341C4F3504B8FA85C75E32F386A3251B5CC31A55385B0B6B45FB4A9AF540E"], ["**/*.mp4", "**/*.dll.user"]);
    }
}
