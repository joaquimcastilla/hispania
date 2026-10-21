namespace Hispania.Runtime;

public sealed class DuplicateModuleIdException(string moduleId)
    : InvalidOperationException($"A module with ID '{moduleId}' is already registered.");

public sealed class ModuleNotFoundException(string moduleId)
    : InvalidOperationException($"No module is registered with ID '{moduleId}'.");

public sealed class ModuleContractNotProvidedException(string moduleId, Type contractType)
    : InvalidOperationException($"Module '{moduleId}' does not provide contract '{contractType.FullName}'.");

public sealed class ModuleStartupException(string moduleId, Exception innerException)
    : InvalidOperationException($"Module '{moduleId}' failed during startup.", innerException);

public sealed class ModuleRuntimePolicyException(string runtime)
    : InvalidOperationException($"Module runtime '{runtime}' is not allowed by the host policy; only 'dotnet-outofprocess' is permitted.");
