using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hispania.Platform.Contracts.Identity;
using Hispania.Playback.Contracts;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class VersioningFoundationTests
{
    private static readonly Regex TypeDeclaration = new(@"^\s*public\s+(?:(?:sealed|abstract|static|readonly|partial)\s+)*(?:record(?:\s+(?:struct|class))?|class|struct|interface|enum)\s+(\w+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex VersionName = new(@"(?:V[12]|Version[12])(?:$|[^0-9])", RegexOptions.CultureInvariant);
    private static readonly string[] Classifications = ["Internal", "Experimental", "Public Experimental", "Stable Candidate", "Public Stable", "Deprecated"];
    private static readonly string[] Fields = ["id", "contract", "source", "kind", "owner", "technicalVersion", "visibility", "stability", "consumers", "serialization", "compatibilityCommitment", "breakingChangePolicy", "interpretation"];
    private static readonly string[] ExperimentalClassifications = ["Experimental", "Public Experimental"];

    [Fact]
    public void EveryCurrentContractAndVersionNamedProductionDeclarationIsClassifiedOnce()
    {
        var root = Root();
        using var catalog = ReadCatalog(root);
        Assert.Equal(1, catalog.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("version.json", catalog.RootElement.GetProperty("distributionSource").GetString());
        var rows = catalog.RootElement.GetProperty("contracts").EnumerateArray().ToArray();
        Assert.Equal(rows.Length, rows.Select(row => row.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
        var markdown = File.ReadAllText(Path.Combine(root, "docs", "compatibility", "CONTRACT-CATALOG.md"));
        foreach (var row in rows)
        {
            foreach (var field in Fields)
                Assert.False(string.IsNullOrWhiteSpace(row.GetProperty(field).GetString()), $"Missing {field} for {row.GetProperty("id")}");
            Assert.Contains(row.GetProperty("stability").GetString(), Classifications);
            Assert.NotEqual("Public Stable", row.GetProperty("stability").GetString());
            Assert.True(File.Exists(Path.Combine(root, row.GetProperty("source").GetString()!)), $"Missing source for {row.GetProperty("id")}");
            Assert.Contains(row.GetProperty("id").GetString()!, markdown, StringComparison.Ordinal);
            if (row.GetProperty("stability").GetString() == "Internal" && row.GetProperty("kind").GetString() == "CLR")
                Assert.False(row.GetProperty("source").GetString()!.StartsWith("contracts/platform/", StringComparison.Ordinal) || row.GetProperty("source").GetString()!.StartsWith("contracts/module-system/", StringComparison.Ordinal), "Internal declarations must not leak into public SDK projects.");
        }

        var actual = new List<string>();
        foreach (var folder in new[] { "contracts", "core", "apps", "application", "infrastructure", "modules" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj" or "node_modules")) continue;
            foreach (Match declaration in TypeDeclaration.Matches(File.ReadAllText(file)))
            {
                var name = declaration.Groups[1].Value;
                if (relative.StartsWith("contracts/", StringComparison.Ordinal) || relative.StartsWith("core/domain/", StringComparison.Ordinal) || VersionName.IsMatch(name))
                    actual.Add(relative + "#" + name);
            }
        }
        var declared = rows.Where(row => row.GetProperty("kind").GetString() == "CLR").Select(row => row.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(actual.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
        foreach (var id in new[] { "http.range", "worker.fencing" })
            Assert.Equal("Stable Candidate", rows.Single(row => row.GetProperty("id").GetString() == id).GetProperty("stability").GetString());
        foreach (var name in new[] { "IIdentityQueryV1", "IIdentityCommandV1", "IAuthorizationServiceV1", "AssetId", "AssetReference", "DirectPlayExecutionResult" })
            Assert.Contains(rows, row => row.GetProperty("contract").GetString() == name && row.GetProperty("stability").GetString() == "Stable Candidate");
        foreach (var id in new[] { "jobs.payload", "events.payload", "package.lifecycle", "web.dto" })
            Assert.Contains(rows.Single(row => row.GetProperty("id").GetString() == id).GetProperty("stability").GetString(), ExperimentalClassifications);
    }

    [Fact]
    public void DistributionMetadataAndLegacyTechnicalVersionsKeepSeparateMeanings()
    {
        var root = Root();
        using var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "version.json")));
        var value = version.RootElement.GetProperty("distributionVersion").GetString()!;
        Assert.Matches(@"^0\.\d+\.\d+$", value);
        Assert.Equal("pre-1.0", version.RootElement.GetProperty("stability").GetString());
        Assert.True(File.Exists(Path.Combine(root, version.RootElement.GetProperty("policy").GetString()!)));
        foreach (var doc in new[] { "VERSIONING.md", "COMPATIBILITY-POLICY.md", "VERSION-MAP.md", "PLATFORM-1.0-SCOPE.md" })
            Assert.Contains(value, File.ReadAllText(Path.Combine(root, "docs", doc)), StringComparison.Ordinal);
        var axisLines = File.ReadAllLines(Path.Combine(root, "docs", "VERSIONING.md"))
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal) && !line.StartsWith("| Axis |", StringComparison.Ordinal) && !line.StartsWith("| ---", StringComparison.Ordinal)).ToArray();
        Assert.Equal(24, axisLines.Length);
        Assert.Equal(24, axisLines.Select(line => line.Split('|')[1].Trim()).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(axisLines, line => line.StartsWith("| Backup Format Generation |", StringComparison.Ordinal) && line.Contains("FormatGeneration = 1", StringComparison.Ordinal));
        using var catalog = ReadCatalog(root);
        var backup = catalog.RootElement.GetProperty("contracts").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "backup.format");
        Assert.Equal("Experimental", backup.GetProperty("stability").GetString());
        Assert.Contains("independent", backup.GetProperty("interpretation").GetString()!, StringComparison.OrdinalIgnoreCase);
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.DoesNotContain("<Version>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<AssemblyVersion>", props, StringComparison.Ordinal);
        using var web = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "apps", "web", "package.json")));
        using var webLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "apps", "web", "package-lock.json")));
        Assert.True(web.RootElement.GetProperty("private").GetBoolean());
        Assert.Equal(web.RootElement.GetProperty("version").GetString(), webLock.RootElement.GetProperty("version").GetString());
        Assert.Equal(web.RootElement.GetProperty("version").GetString(), webLock.RootElement.GetProperty("packages").GetProperty("").GetProperty("version").GetString());
        foreach (var file in new[] { "apps/server/Program.cs", "apps/worker-agent/Program.cs", "apps/module-host/Program.cs" })
            Assert.Contains("new Version(1, 0, 0)", File.ReadAllText(Path.Combine(root, file)), StringComparison.Ordinal);
        Assert.Equal(1, Hispania.ModuleSystem.Contracts.ModuleWorkerProtocolV1.Version);
        Assert.Equal(1, new Hispania.ModuleSystem.Contracts.ModuleManifest { Id = "test", Name = "test", Version = "1.0.0", Implements = [], MinimumHostVersion = "1.0.0", Runtime = "dotnet-inprocess-trusted" }.PackageFormatVersion);
        foreach (var file in new[] { "core/runtime/DistributedWorkerAgent.cs", "apps/server/DistributedWorkerEndpoints.cs" })
            Assert.Matches(@"const int ProtocolVersion = " + DistributedWorkerAgent.ProtocolVersion.ToString(CultureInfo.InvariantCulture) + ";", File.ReadAllText(Path.Combine(root, file)));
    }

    [Fact]
    public void SelectedCandidateCompiledApiMatchesReviewedBaseline()
    {
        var root = Root();
        using var catalog = ReadCatalog(root);
        var selected = catalog.RootElement.GetProperty("contracts").EnumerateArray()
            .Where(row => row.GetProperty("kind").GetString() == "CLR" && row.GetProperty("stability").GetString() == "Stable Candidate")
            .Select(row => row.GetProperty("contract").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assembly[] assemblies = [typeof(IIdentityQueryV1).Assembly, typeof(DirectPlayExecutionResult).Assembly];
        var types = assemblies.SelectMany(assembly => assembly.GetExportedTypes()).Where(type => selected.Contains(type.Name.Split('`')[0])).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        Assert.Equal(selected.Count, types.Length);
        var nullability = new NullabilityInfoContext();
        var lines = new List<string>();
        foreach (var type in types)
        {
            lines.Add($"TYPE {type.FullName} | base={type.BaseType} | interfaces={string.Join(",", type.GetInterfaces().Select(item => item.ToString()).Order(StringComparer.Ordinal))}");
            foreach (var argument in type.GetGenericArguments())
                lines.Add($"GENERIC {argument.Name} {argument.GenericParameterAttributes} constraints=[{string.Join(",", argument.GetGenericParameterConstraints().Select(item => item.ToString()).Order(StringComparer.Ordinal))}]");
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(member => member.ToString(), StringComparer.Ordinal))
            {
                if (member is Type) continue;
                var detail = member switch
                {
                    MethodInfo method => $"static={method.IsStatic};abstract={method.IsAbstract};virtual={method.IsVirtual};return={NullableShape(nullability.Create(method.ReturnParameter))};params={Parameters(method.GetParameters(), nullability)}",
                    ConstructorInfo constructor => "params=" + Parameters(constructor.GetParameters(), nullability),
                    PropertyInfo property => $"null={NullableShape(nullability.Create(property))};get={property.GetMethod?.IsPublic};set={property.SetMethod?.IsPublic}",
                    FieldInfo field => $"null={NullableShape(nullability.Create(field))};value={(field.IsLiteral ? Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : "nonconstant")}",
                    _ => ""
                };
                lines.Add($"{member.MemberType} {member} | {detail}");
            }
        }
        var actual = string.Join('\n', lines) + "\n";
        var path = Path.Combine(root, "compatibility", "api", "stable-candidates.txt");
        if (Environment.GetEnvironmentVariable("HISPANIA_CAPTURE_API_BASELINE") == "1")
        {
            Assert.False(File.Exists(Path.Combine(root, "compatibility", "baseline-0.7.json")), "Historical baseline exists: preserve 0.7; capture new evidence in a new staging corpus.");
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase), "CI must not regenerate API evidence.");
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))).ToLowerInvariant();
        Assert.Contains(hash, File.ReadAllText(Path.Combine(root, "compatibility", "api", "CHANGELOG.md")), StringComparison.Ordinal);
    }

    private static string Parameters(ParameterInfo[] parameters, NullabilityInfoContext context) => string.Join(";", parameters.Select(parameter => $"{parameter.Name}:{parameter.ParameterType}:{NullableShape(context.Create(parameter))}:in={parameter.IsIn}:out={parameter.IsOut}:optional={parameter.IsOptional}:default={(parameter.HasDefaultValue ? parameter.DefaultValue is null ? "null" : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) : "none")}"));
    private static string NullableShape(NullabilityInfo value) => $"{value.ReadState}/{value.WriteState}[{string.Join(",", value.GenericTypeArguments.Select(NullableShape))}]{(value.ElementType is null ? "" : NullableShape(value.ElementType))}";
    private static JsonDocument ReadCatalog(string root) => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "compatibility", "contracts.json")));
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
