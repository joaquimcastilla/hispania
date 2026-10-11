namespace Hispania.ModuleSystem.Contracts;

/// <summary>Declarative metadata for a native module. Metadata is never executable.</summary>
public sealed record ModuleManifest
{
    /// <summary>Format-2 package implementation SemVer, independent of the CLR module version.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageVersion { get; init; }
    /// <summary>Format-2 required package dependencies. Null preserves historical signing bytes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PackageDependency[]? PackageDependencies { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? RequiredModuleHostProtocol { get; init; }
    /// <summary>Signed, host-validated declarations of Platform Job handlers. Null preserves historical manifest signatures.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModuleJobContributionV1[]? JobContributions { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string[] Implements { get; init; }
    public string[] Provides { get; init; } = [];
    public string[] Requires { get; init; } = [];
    public string[] OptionalDependencies { get; init; } = [];
    public string[] Dependencies { get; init; } = [];
    /// <summary>Optional minimum version per required module ID.</summary>
    public Dictionary<string, string> DependencyMinimumVersions { get; init; } = new(StringComparer.Ordinal);
    public string[] Permissions { get; init; } = [];
    public required string MinimumHostVersion { get; init; }
    public string? MaximumHostVersion { get; init; }
    /// <summary>Execution mode is explicit; existing packages remain in-process.</summary>
    public required string Runtime { get; init; }
    public bool RestartRequiredForUpdate { get; init; } = true;
    /// <summary>Package-relative assembly that contains the module entry point.</summary>
    public string? EntryAssembly { get; init; }
    /// <summary>Assembly qualified type name implementing IModule.</summary>
    public string? EntryType { get; init; }
    /// <summary>Public contract assembly simple names resolved from the host default context.</summary>
    public string[] SharedAssemblies { get; init; } = [];
    /// <summary>Maps a declared capability ID@version to its public CLR interface full name for IPC dispatch.</summary>
    public Dictionary<string, string> ContractTypes { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Publisher key identity. Signature covers canonical manifest metadata and the sorted payload digest map.</summary>
    public string? PublisherId { get; init; }
    public string? SignatureAlgorithm { get; init; }
    public string? Signature { get; init; }
    /// <summary>SHA-256 digests for every file in a versioned .hispania package, keyed by package-relative path.</summary>
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Version of the .hispania package container format.</summary>
    public int PackageFormatVersion { get; init; } = 1;
}

/// <summary>Declares one Job type served through a public contribution capability implemented by this module.</summary>
public sealed record ModuleJobContributionV1(string JobType, string CapabilityId, string CapabilityVersion);

public sealed record PackageDependency(string PackageId, string VersionRange);
