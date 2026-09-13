namespace Hispania.ModuleSystem.Contracts;

public sealed record ModuleDescriptor
{
    public ModuleDescriptor(string id, string name, Version version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(version);

        Id = id;
        Name = name;
        Version = version;
    }

    public string Id { get; }

    public string Name { get; }

    public Version Version { get; }
}
