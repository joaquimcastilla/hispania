using Hispania.Kernel;

namespace Hispania.Runtime;

public sealed record ModuleHealth(
    ModuleDescriptor Descriptor,
    ModuleLifecycleState State,
    bool IsHealthy,
    string? Detail);
