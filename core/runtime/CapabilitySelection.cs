using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

/// <summary>Resolves one implementation using an explicit module ID or a declared versioned contract.</summary>
public interface ICapabilityResolver
{
    TContract Resolve<TContract>(string contractId, Version contractVersion, string? implementationId = null)
        where TContract : class;
}

public sealed record CapabilityVariant<TContract>(string ImplementationId, TContract Implementation)
    where TContract : class;

public sealed class CapabilityResolver(IModuleRegistry registry) : ICapabilityResolver
{
    public TContract Resolve<TContract>(string contractId, Version contractVersion, string? implementationId = null)
        where TContract : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentNullException.ThrowIfNull(contractVersion);

        var candidates = registry.List()
            .Where(module => module.Descriptor.Provides.Any(capability =>
                string.Equals(capability.ContractId, contractId, StringComparison.Ordinal) &&
                capability.Version == contractVersion))
            .Where(module => implementationId is null || string.Equals(module.Descriptor.Id, implementationId, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new CapabilityUnavailableException(contractId, contractVersion, implementationId);
        }

        if (candidates.Length > 1)
        {
            throw new AmbiguousCapabilityException(contractId, contractVersion, candidates.Select(module => module.Descriptor.Id));
        }

        if (candidates[0] is TContract implementation) return implementation;
        if (candidates[0] is IModuleCapabilityProxyFactory proxyFactory)
            return proxyFactory.CreateCapabilityProxy(typeof(TContract)) as TContract
                ?? throw new ModuleContractNotProvidedException(candidates[0].Descriptor.Id, typeof(TContract));
        throw new ModuleContractNotProvidedException(candidates[0].Descriptor.Id, typeof(TContract));
    }
}

public sealed class CapabilityUnavailableException(string contractId, Version version, string? implementationId)
    : InvalidOperationException(implementationId is null
        ? $"No implementation provides '{contractId}@{version}'."
        : $"Module '{implementationId}' does not provide '{contractId}@{version}'.");

public sealed class AmbiguousCapabilityException(string contractId, Version version, IEnumerable<string> moduleIds)
    : InvalidOperationException($"Capability '{contractId}@{version}' has multiple implementations: {string.Join(", ", moduleIds.Order(StringComparer.Ordinal))}.");

/// <summary>Executes an optional comparison implementation while returning only the selected implementation's result.</summary>
public sealed class ShadowCapability<TInput, TResult>(
    string activeImplementationId,
    Func<TInput, TResult> active,
    string shadowImplementationId,
    Func<TInput, TResult> shadow,
    Action<ShadowComparison<TResult>>? observe = null)
{
    public TResult Execute(TInput input)
    {
        var activeResult = active(input);
        TResult? shadowResult = default;
        Exception? failure = null;
        try
        {
            shadowResult = shadow(input);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        observe?.Invoke(new ShadowComparison<TResult>(activeImplementationId, shadowImplementationId, activeResult, shadowResult, failure));
        return activeResult;
    }
}

public sealed record ShadowComparison<TResult>(
    string ActiveImplementationId,
    string ShadowImplementationId,
    TResult ActiveResult,
    TResult? ShadowResult,
    Exception? ShadowFailure);
