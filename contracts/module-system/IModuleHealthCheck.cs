namespace Hispania.ModuleSystem.Contracts;

/// <summary>Optional operational readiness check for a registered module.</summary>
public interface IModuleHealthCheck
{
    ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}
