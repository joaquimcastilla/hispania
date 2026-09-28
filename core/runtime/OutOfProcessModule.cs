using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

/// <summary>Supervises one explicitly authorized extension process over a private local named pipe.</summary>
internal sealed class OutOfProcessModule : IModule, IModuleHealthCheck, IModuleCapabilityProxyFactory, IDisposable
{
    private static readonly Meter ModuleMeter = new("Hispania.Runtime.OutOfProcess", "1.0");
    private static readonly Counter<long> ProcessStarts = ModuleMeter.CreateCounter<long>("hispania.modules.outofprocess.starts");
    private static readonly Counter<long> ProcessRestarts = ModuleMeter.CreateCounter<long>("hispania.modules.outofprocess.restarts");
    private static readonly Counter<long> ProcessFailures = ModuleMeter.CreateCounter<long>("hispania.modules.outofprocess.failures");
    private static readonly Counter<long> Requests = ModuleMeter.CreateCounter<long>("hispania.modules.outofprocess.requests");
    private static readonly Histogram<double> RequestDuration = ModuleMeter.CreateHistogram<double>("hispania.modules.outofprocess.request.duration", "ms");
    private readonly string packageDirectory;
    private readonly ModuleManifest manifest;
    private readonly string hostAssemblyPath;
    private readonly Action<string>? markDegraded;
    private readonly OutOfProcessResourceLimits resourceLimits;
    private readonly OutOfProcessRestartPolicy restartPolicy;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim requestSlots;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ModuleWorkerFrameV1>> pending = new(StringComparer.Ordinal);
    private NamedPipeServerStream? pipe;
    private Process? process;
    private IDisposable? processLimits;
    private Task? readLoop;
    private int restartCount;
    private DateTimeOffset restartWindowStarted;
    private volatile bool healthy;
    private readonly StringBuilder hostErrors = new();

    public OutOfProcessModule(string packageDirectory, ModuleManifest manifest, string hostAssemblyPath, Action<string>? markDegraded = null, OutOfProcessResourceLimits? resourceLimits = null, OutOfProcessRestartPolicy? restartPolicy = null)
    {
        this.packageDirectory = Path.GetFullPath(packageDirectory);
        this.manifest = manifest;
        this.hostAssemblyPath = Path.GetFullPath(hostAssemblyPath);
        this.markDegraded = markDegraded;
        this.resourceLimits = resourceLimits ?? new OutOfProcessResourceLimits();
        this.resourceLimits.Validate();
        this.restartPolicy = restartPolicy ?? new OutOfProcessRestartPolicy();
        this.restartPolicy.Validate();
        requestSlots = new SemaphoreSlim(this.resourceLimits.MaximumConcurrentRequests, this.resourceLimits.MaximumConcurrentRequests);
        Descriptor = BuildDescriptor(manifest);
    }

    public ModuleDescriptor Descriptor { get; }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (healthy && process is { HasExited: false }) return;
            if (process is not null && process.HasExited)
            {
                processLimits?.Dispose(); processLimits = null; process.Dispose(); process = null; pipe?.Dispose(); pipe = null;
            }
            if (restartWindowStarted != default && DateTimeOffset.UtcNow - restartWindowStarted > restartPolicy.RecoveryWindow)
            { restartWindowStarted = default; restartCount = 0; }
            if (restartWindowStarted != default)
            {
                if (restartCount >= restartPolicy.MaximumRestarts) throw new InvalidOperationException("Out-of-process module restart limit reached for the current recovery window.");
                ProcessRestarts.Add(1, MetricTags());
                await Task.Delay(restartPolicy.GetBackoff(restartCount++), cancellationToken).ConfigureAwait(false);
            }
            await StartProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(hostAssemblyPath)) throw new FileNotFoundException("Generic module host executable is missing.", hostAssemblyPath);
        var pipeName = "hispania-" + Guid.NewGuid().ToString("N");
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, ModuleWorkerProtocolV1.DefaultMaximumFrameBytes, ModuleWorkerProtocolV1.DefaultMaximumFrameBytes);
        hostErrors.Clear();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(hostAssemblyPath);
        start.ArgumentList.Add("--package"); start.ArgumentList.Add(packageDirectory);
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        const string tokenEnvironment = "HISPANIA_MODULE_LAUNCH_TOKEN";
        start.ArgumentList.Add("--token-env"); start.ArgumentList.Add(tokenEnvironment);
        start.Environment[tokenEnvironment] = token;
        resourceLimits.Configure(start);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the generic module host.");
        ProcessStarts.Add(1, MetricTags());
        processLimits = resourceLimits.ApplyTo(process);
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (hostErrors) hostErrors.AppendLine(eventArgs.Data); };
        process.BeginErrorReadLine();
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var connection = pipe.WaitForConnectionAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                throw new InvalidOperationException($"Module host exited during startup with code {process.ExitCode}. {hostErrors}");
            }
            await connection.ConfigureAwait(false);
            var frame = await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(pipe, cancellationToken: deadline.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Module host closed before authentication.");
            if (frame.Kind != "hello") throw new InvalidDataException("Expected module host hello message.");
            var hello = frame.Decode<ModuleWorkerHelloV1>();
            var negotiation = ModuleWorkerProtocolV1.Negotiate(hello, manifest.Id, manifest.Version, token);
            await ModuleWorkerProtocolV1.WriteFrameAsync(pipe, ModuleWorkerFrameV1.From("negotiation", negotiation), cancellationToken: deadline.Token).ConfigureAwait(false);
            if (!negotiation.Accepted) throw new UnauthorizedAccessException(negotiation.Error ?? "Module protocol negotiation failed.");
            var capabilitiesFrame = await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(pipe, cancellationToken: deadline.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException($"Module host exited before capability discovery. {hostErrors}");
            if (capabilitiesFrame.Kind != "capabilities") throw new InvalidDataException("Expected module capability inventory.");
            var inventory = capabilitiesFrame.Decode<ModuleWorkerCapabilitiesV1>();
            var expected = manifest.Provides.Order(StringComparer.Ordinal).ToArray();
            var actual = inventory.Provides.Select(item => $"{item.ContractId}@{item.ContractVersion}").Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) throw new InvalidDataException("Module host capabilities differ from the authorized manifest.");
            healthy = true;
            readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
        }
        catch
        {
            ProcessFailures.Add(1, MetricTags());
            healthy = false;
            FailPending(new IOException("Module host failed during startup."));
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            pipe.Dispose(); pipe = null; process?.Dispose(); process = null;
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (pipe is not null)
            {
                var frame = await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(pipe, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (frame is null) break;
                var correlationId = frame.Kind switch
                {
                    "response" => frame.Decode<ModuleWorkerResponseV1>().RequestId,
                    "health" => frame.Decode<ModuleWorkerHealthResponseV1>().CorrelationId,
                    _ => throw new InvalidDataException($"Unexpected host response frame '{frame.Kind}'.")
                };
                if (pending.TryGetValue(correlationId, out var completion)) completion.TrySetResult(frame);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
        {
            FailPending(new IOException("Out-of-process module host communication failed.", exception));
        }
        finally
        {
            var wasHealthy = healthy;
            healthy = false;
            FailPending(new IOException("Out-of-process module host exited."));
            if (wasHealthy)
            {
                ProcessFailures.Add(1, MetricTags());
                markDegraded?.Invoke("Out-of-process module host exited unexpectedly.");
            }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in pending.Values) completion.TrySetException(exception);
    }

    private async Task SendFrameAsync(ModuleWorkerFrameV1 frame, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pipe is null || process is not { HasExited: false }) throw new IOException("Out-of-process module host is not connected.");
            await ModuleWorkerProtocolV1.WriteFrameAsync(pipe, frame, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wasHealthy = healthy;
            healthy = false;
            if (pipe is not null && process is { HasExited: false })
            {
                try { await SendFrameAsync(ModuleWorkerFrameV1.From("stop", new ModuleWorkerStopV1("host shutdown")), cancellationToken).ConfigureAwait(false); } catch (IOException) { }
                try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            pipe?.Dispose(); pipe = null; processLimits?.Dispose(); processLimits = null; process?.Dispose(); process = null;
            if (readLoop is not null) { try { await readLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch (TimeoutException) { } readLoop = null; }
            if (!wasHealthy && restartWindowStarted == default) restartWindowStarted = DateTimeOffset.UtcNow;
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (!healthy || process is not { HasExited: false } || pipe?.IsConnected != true) return false;
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ModuleWorkerFrameV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Could not allocate a module health correlation ID.");
        try
        {
            await SendFrameAsync(ModuleWorkerFrameV1.From("health", new ModuleWorkerHealthRequestV1(id)), cancellationToken).ConfigureAwait(false);
            // CI runners and Windows Job Object constrained processes can be
            // briefly starved while their runtime initializes or performs GC.
            var frame = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (frame?.Kind != "health") { healthy = false; return false; }
            var response = frame.Decode<ModuleWorkerHealthResponseV1>();
            healthy = response.CorrelationId == id && response.Healthy;
            return healthy;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { healthy = false; return false; }
        catch (IOException) { healthy = false; return false; }
        finally { pending.TryRemove(id, out _); }
    }

    public object CreateCapabilityProxy(Type contractType)
    {
        if (!contractType.IsInterface) throw new ModuleContractNotProvidedException(Descriptor.Id, contractType);
        foreach (var method in contractType.GetMethods())
        {
            if (method.ReturnType.IsByRef || method.ReturnType.IsPointer || method.GetParameters().Any(parameter => parameter.IsOut || parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer) ||
                method.GetParameters().Count(parameter => parameter.ParameterType != typeof(CancellationToken)) > 1)
                throw new NotSupportedException($"Contract operation '{method.Name}' is not supported by module protocol V1.");
        }
        var capability = manifest.Provides.Select(ParseCapability).FirstOrDefault(item =>
            manifest.ContractTypes.TryGetValue($"{item.Id}@{item.Version}", out var fullName) && fullName == contractType.FullName);
        if (capability.Id is null) throw new ModuleContractNotProvidedException(Descriptor.Id, contractType);
        var proxy = DispatchProxy.Create(contractType, typeof(RemoteContractDispatchProxy));
        ((RemoteContractDispatchProxy)proxy).Initialize(this, capability.Id, capability.Version);
        return proxy;
    }

    internal object? Invoke(string contractId, string contractVersion, MethodInfo method, object?[] arguments)
    {
        if (!healthy || process is not { HasExited: false } || pipe is null)
        {
            healthy = false;
            throw new IOException($"Out-of-process module '{Descriptor.Id}' is unavailable; reactivate it to recover.");
        }
        var callerCancellation = method.GetParameters().Select((parameter, index) => (parameter, index))
            .Where(item => item.parameter.ParameterType == typeof(CancellationToken)).Select(item => (CancellationToken)arguments[item.index]!).SingleOrDefault();
        requestSlots.Wait(callerCancellation);
        var requestStopwatch = Stopwatch.StartNew();
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var methodParameters = method.GetParameters();
            var operationArgument = methodParameters.Select((parameter, index) => (parameter, index))
                .Where(item => item.parameter.ParameterType != typeof(CancellationToken)).Select(item => arguments[item.index]).SingleOrDefault();
            using var empty = JsonDocument.Parse("{}");
            var payload = operationArgument is null ? empty.RootElement.Clone() : JsonSerializer.SerializeToElement(operationArgument, operationArgument.GetType());
            var request = new ModuleWorkerRequestV1(requestId, contractId, contractVersion, method.Name, payload);
            var completion = new TaskCompletionSource<ModuleWorkerFrameV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Could not allocate a module request ID.");
            try
            {
                SendFrameAsync(ModuleWorkerFrameV1.From("request", request), callerCancellation).GetAwaiter().GetResult();
                ModuleWorkerFrameV1 frame;
                try { frame = completion.Task.WaitAsync(resourceLimits.MaximumRequestDuration, callerCancellation).GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    SendFrameAsync(ModuleWorkerFrameV1.From("cancel", new ModuleWorkerCancelV1(requestId)), CancellationToken.None).GetAwaiter().GetResult();
                    try { completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                    catch (TimeoutException) { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); healthy = false; }
                    callerCancellation.ThrowIfCancellationRequested();
                    throw new TimeoutException($"Out-of-process module '{Descriptor.Id}' exceeded its {resourceLimits.MaximumRequestDuration.TotalSeconds:0} second request limit.");
                }
                if (frame.Kind != "response") throw new InvalidDataException("Expected module response frame.");
                var response = frame.Decode<ModuleWorkerResponseV1>();
                if (response.RequestId != requestId) throw new InvalidDataException("Module response correlation ID does not match.");
                if (!response.Succeeded && response.Error?.Code == "cancelled") throw new OperationCanceledException(callerCancellation);
                if (!response.Succeeded) throw new InvalidOperationException(response.Error?.Message ?? "Out-of-process module operation failed.");
                if (method.ReturnType == typeof(void)) return null;
                var returnType = method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) ? method.ReturnType.GetGenericArguments()[0] : method.ReturnType;
                return response.Payload?.Deserialize(returnType);
            }
            finally { pending.TryRemove(requestId, out _); }
        }
        catch (OperationCanceledException exception) when (!callerCancellation.IsCancellationRequested)
        {
            healthy = false;
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            markDegraded?.Invoke("Out-of-process module request timed out.");
            throw new TimeoutException($"Out-of-process module '{Descriptor.Id}' exceeded its {resourceLimits.MaximumRequestDuration.TotalSeconds:0} second request limit.", exception);
        }
        catch (IOException) { healthy = false; markDegraded?.Invoke("Out-of-process module process exited unexpectedly."); throw; }
        finally
        {
            requestStopwatch.Stop();
            Requests.Add(1, MetricTags());
            RequestDuration.Record(requestStopwatch.Elapsed.TotalMilliseconds, MetricTags());
            requestSlots.Release();
        }
    }

    public void Dispose()
    {
        try { StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(); } catch { }
        processLimits?.Dispose();
        lifecycle.Dispose(); writeGate.Dispose(); requestSlots.Dispose();
    }

    private static ModuleDescriptor BuildDescriptor(ModuleManifest manifest)
    {
        static ModuleCapability Convert(string item) { var parsed = ParseCapability(item); return new(parsed.Id, Version.Parse(parsed.Version)); }
        var dependencies = manifest.Dependencies.Select(id => new ModuleDependency(id, Version.Parse(manifest.DependencyMinimumVersions.GetValueOrDefault(id) ?? "0.0")));
        return new ModuleDescriptor(manifest.Id, manifest.Name, Version.Parse(manifest.Version), manifest.Provides.Select(Convert), manifest.Requires.Select(Convert), dependencies);
    }
    private KeyValuePair<string, object?>[] MetricTags() => [new("module.id", Descriptor.Id), new("module.version", Descriptor.Version.ToString())];
    private static (string Id, string Version) ParseCapability(string item)
    {
        var at = item.LastIndexOf('@');
        if (at <= 0 || !Version.TryParse(item[(at + 1)..], out _)) throw new InvalidDataException($"Invalid capability '{item}'.");
        return (item[..at], item[(at + 1)..]);
    }
}

public class RemoteContractDispatchProxy : DispatchProxy
{
    private OutOfProcessModule? module;
    private string contractId = "";
    private string contractVersion = "";
    internal void Initialize(OutOfProcessModule owner, string id, string version) { module = owner; contractId = id; contractVersion = version; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null || module is null) throw new InvalidOperationException("Remote contract proxy is not initialized.");
        if (targetMethod.DeclaringType == typeof(object))
            return targetMethod.Name switch { nameof(ToString) => $"Remote module contract {contractId}@{contractVersion}", nameof(GetHashCode) => HashCode.Combine(contractId, contractVersion), nameof(Equals) => ReferenceEquals(args?.FirstOrDefault(), this), _ => null };
        var arguments = args ?? [];
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task)) return Task.Run(() => module.Invoke(contractId, contractVersion, targetMethod, arguments));
        if (returnType == typeof(ValueTask)) return new ValueTask(Task.Run(() => module.Invoke(contractId, contractVersion, targetMethod, arguments)));
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(RemoteContractDispatchProxy).GetMethod(nameof(InvokeTask), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(this, [targetMethod, arguments]);
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var task = typeof(RemoteContractDispatchProxy).GetMethod(nameof(InvokeTask), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(this, [targetMethod, arguments]);
            return Activator.CreateInstance(returnType, task);
        }
        return module.Invoke(contractId, contractVersion, targetMethod, arguments);
    }

    private Task<T> InvokeTask<T>(MethodInfo method, object?[] arguments) => Task.Run(() => (T)module!.Invoke(contractId, contractVersion, method, arguments)!);
}
