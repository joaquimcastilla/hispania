namespace Hispania.ModuleSystem.Contracts;

public sealed record ModuleDescriptor
{
    public ModuleDescriptor(string id, string name, Version version, IEnumerable<ModuleCapability>? provides = null, IEnumerable<ModuleCapability>? requires = null, IEnumerable<ModuleDependency>? dependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(version);

        Id = id;
        Name = name;
        Version = version;
        Provides = Array.AsReadOnly((provides ?? []).Distinct().OrderBy(x => x.ContractId, StringComparer.Ordinal).ThenBy(x => x.Version).ToArray());
        Requires = Array.AsReadOnly((requires ?? []).Distinct().OrderBy(x => x.ContractId, StringComparer.Ordinal).ThenBy(x => x.Version).ToArray());
        Dependencies = Array.AsReadOnly((dependencies ?? []).Distinct().OrderBy(x => x.ModuleId, StringComparer.Ordinal).ToArray());
    }

    public string Id { get; }

    public string Name { get; }

    public Version Version { get; }

    public IReadOnlyCollection<ModuleCapability> Provides { get; }

    public IReadOnlyCollection<ModuleCapability> Requires { get; }

    public IReadOnlyCollection<ModuleDependency> Dependencies { get; }
}

/// <summary>A versioned capability identifier advertised or required by a module.</summary>
public sealed record ModuleCapability
{
    public ModuleCapability(string contractId, Version version, bool dynamicallyReplaceable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentNullException.ThrowIfNull(version);
        ContractId = contractId;
        Version = version;
        DynamicallyReplaceable = dynamicallyReplaceable;
    }

    public string ContractId { get; }

    public Version Version { get; }

    /// <summary>True only when the provider permits runtime replacement for this capability.</summary>
    public bool DynamicallyReplaceable { get; }
}

/// <summary>Required module and minimum compatible implementation version.</summary>
public sealed record ModuleDependency
{
    public ModuleDependency(string moduleId, Version minimumVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(minimumVersion);
        ModuleId = moduleId;
        MinimumVersion = minimumVersion;
    }

    public string ModuleId { get; }
    public Version MinimumVersion { get; }
}
