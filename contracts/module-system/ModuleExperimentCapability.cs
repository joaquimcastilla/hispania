namespace Hispania.ModuleSystem.Contracts;

public enum ModuleExperimentMode { Manual, Shadow, TrafficSplit }

/// <summary>Opt-in modes for one exact public capability. Shadow requires a named, read-only dependency boundary.</summary>
public sealed record ModuleExperimentCapability(string ContractId, Version ContractVersion,
    IReadOnlyCollection<ModuleExperimentMode> Modes, string? ShadowReadOnlyBoundary = null);

/// <summary>Optional declaration; absence means the module is not eligible for experiments.</summary>
public interface IModuleExperimentDeclaration
{
    IReadOnlyCollection<ModuleExperimentCapability> ExperimentCapabilities { get; }
}
