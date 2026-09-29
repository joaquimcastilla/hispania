using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public interface IDistributedCapabilityExecutorV1
{
    IReadOnlyCollection<DistributedCapabilityV1> ListCapabilities();
    ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, CancellationToken cancellationToken);
    ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
}

/// <summary>Optional capability handler for streaming or other job-level facilities that do not fit JSON-only extension calls.</summary>
public interface IDistributedCapabilityHandlerV1
{
    IReadOnlyCollection<DistributedCapabilityV1> Capabilities { get; }
    ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken);
}

/// <summary>Worker capability plug-in boundary. Product-specific providers live outside the generic worker host.</summary>
public interface IDistributedWorkerCapabilityProviderV1
{
    IReadOnlyCollection<IDistributedCapabilityHandlerV1> CreateHandlers(DistributedWorkerCapabilityContextV1 context);
}

public sealed record DistributedWorkerCapabilityContextV1(Uri CoordinatorUri, string NodeId,
    X509Certificate2 ClientCertificate, IReadOnlyCollection<string> CoordinatorPins,
    IReadOnlyDictionary<string, string> Configuration);

/// <summary>Composes specialized versioned handlers with the generic trusted-extension executor.</summary>
public sealed class CompositeDistributedCapabilityExecutor(IDistributedCapabilityExecutorV1 extensions, IEnumerable<IDistributedCapabilityHandlerV1> handlers) : IDistributedCapabilityExecutorV1
{
    private readonly IReadOnlyList<IDistributedCapabilityHandlerV1> handlers = handlers.ToArray();
    public IReadOnlyCollection<DistributedCapabilityV1> ListCapabilities() => extensions.ListCapabilities().Concat(handlers.SelectMany(static handler => handler.Capabilities)).Distinct().ToArray();
    public ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, CancellationToken cancellationToken) => ExecuteAsync(request, 0, cancellationToken);
    public ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken)
    {
        var capability = new DistributedCapabilityV1(request.ContractId, request.ContractVersion);
        var matching = handlers.Where(handler => handler.Capabilities.Contains(capability)).ToArray();
        if (matching.Length > 1) throw new InvalidOperationException($"More than one distributed handler provides {capability}.");
        return matching.Length == 1 ? matching[0].ExecuteAsync(request, generation, cancellationToken) : extensions.ExecuteAsync(request, cancellationToken);
    }
}

/// <summary>Invokes an explicitly installed module through the extension catalog; OOP modules keep their local host boundary.</summary>
public sealed class ModuleExtensionCapabilityExecutor(
    ModuleExtensionCatalog catalog,
    Func<string, Type?> contractTypeResolver) : IDistributedCapabilityExecutorV1
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<DistributedCapabilityV1> ListCapabilities() => catalog.List()
        .Where(static extension => extension.Compatible && extension.Trusted && extension.Loaded && extension.State == ModuleLifecycleState.Running)
        .SelectMany(static extension => extension.Manifest.Provides)
        .Select(static value => ParseCapability(value))
        .Distinct()
        .ToArray();

    public async ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var capability = $"{request.ContractId}@{request.ContractVersion}";
        var providers = catalog.List().Where(extension => extension.Compatible && extension.Trusted && extension.Loaded &&
            extension.State == ModuleLifecycleState.Running && extension.Manifest.Provides.Contains(capability, StringComparer.Ordinal)).ToArray();
        if (providers.Length != 1) throw new InvalidOperationException(providers.Length == 0
            ? $"No active trusted extension provides {capability}."
            : $"Capability {capability} has more than one active extension provider.");

        var contractTypeName = providers[0].Manifest.ContractTypes.GetValueOrDefault(capability)
            ?? throw new InvalidOperationException($"Capability {capability} has no declared CLR contract type.");
        var contractType = contractTypeResolver(contractTypeName)
            ?? throw new InvalidOperationException($"Public contract type '{contractTypeName}' is not installed in the worker host.");
        if (!contractType.IsInterface) throw new InvalidOperationException("Distributed module contracts must be public interfaces.");

        var method = contractType.GetMethods().SingleOrDefault(candidate => candidate.Name == request.Operation)
            ?? throw new InvalidOperationException($"Operation '{request.Operation}' is missing or ambiguous on {contractTypeName}.");
        var parameters = method.GetParameters();
        var dataParameters = parameters.Where(static parameter => parameter.ParameterType != typeof(CancellationToken)).ToArray();
        if (dataParameters.Length > 1) throw new InvalidOperationException("Distributed protocol V1 supports at most one operation payload argument.");
        var arguments = parameters.Select(parameter => parameter.ParameterType == typeof(CancellationToken)
            ? (object?)cancellationToken
            : request.Payload.Deserialize(parameter.ParameterType, JsonOptions)).ToArray();

        var acquire = typeof(ModuleExtensionCatalog).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.Name == nameof(ModuleExtensionCatalog.Acquire) && candidate.IsGenericMethodDefinition)
            .MakeGenericMethod(contractType);
        using var lease = (IDisposable)(acquire.Invoke(catalog, [providers[0].Manifest.Id])
            ?? throw new InvalidOperationException("Module lease acquisition returned no lease."));
        var target = lease.GetType().GetProperty(nameof(ExtensionLease<object>.Value))?.GetValue(lease)
            ?? throw new InvalidOperationException("Module lease did not expose its capability instance.");
        cancellationToken.ThrowIfCancellationRequested();
        object? result;
        try { result = method.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        result = await AwaitResultAsync(result, method.ReturnType).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, UnwrapResultType(method.ReturnType), JsonOptions);
    }

    private static DistributedCapabilityV1 ParseCapability(string value)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || !Version.TryParse(value[(at + 1)..], out var version)) throw new InvalidDataException("Extension capability declaration is malformed.");
        return new DistributedCapabilityV1(value[..at], version.ToString());
    }

    private static async Task<object?> AwaitResultAsync(object? result, Type returnType)
    {
        if (returnType == typeof(void)) return null;
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return returnType.IsGenericType ? returnType.GetProperty("Result")!.GetValue(result) : null;
        }
        if (returnType == typeof(ValueTask)) { await ((ValueTask)result!).ConfigureAwait(false); return null; }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var taskResult = (Task)returnType.GetMethod("AsTask")!.Invoke(result, null)!;
            await taskResult.ConfigureAwait(false);
            return taskResult.GetType().GetProperty("Result")!.GetValue(taskResult);
        }
        return result;
    }

    private static Type UnwrapResultType(Type type) => type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        ? type.GetGenericArguments()[0]
        : type == typeof(Task) || type == typeof(ValueTask) || type == typeof(void) ? typeof(object) : type;
}
