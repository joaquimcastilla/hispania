using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;

return await ModuleHost.RunAsync(args);

internal static class ModuleHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(15_000);
            await ModuleWorkerProtocolV1.WriteFrameAsync(pipe,
                ModuleWorkerFrameV1.From("hello", new ModuleWorkerHelloV1(options.Manifest.Id, options.Manifest.Version,
                    ModuleWorkerProtocolV1.ProtocolId, ModuleWorkerProtocolV1.Version, ModuleWorkerProtocolV1.Version, options.Token)));
            var negotiationFrame = await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(pipe)
                ?? throw new EndOfStreamException("Host closed before protocol negotiation.");
            if (negotiationFrame.Kind != "negotiation") throw new InvalidDataException("Expected protocol negotiation response.");
            var negotiation = negotiationFrame.Decode<ModuleWorkerNegotiationV1>();
            if (!negotiation.Accepted || negotiation.SelectedProtocolVersion != ModuleWorkerProtocolV1.Version)
                throw new InvalidDataException(negotiation.Error ?? "Host rejected the protocol version.");

            var (context, module) = LoadModule(options.Manifest, options.PackageDirectory);
            try
            {
                await module.StartAsync(CancellationToken.None);
                var capabilities = new ModuleWorkerCapabilitiesV1(
                    module.Descriptor.Provides.Select(item => new ModuleWorkerCapabilityV1(item.ContractId, item.Version.ToString())).ToArray(),
                    module.Descriptor.Requires.Select(item => new ModuleWorkerCapabilityV1(item.ContractId, item.Version.ToString())).ToArray());
                await ModuleWorkerProtocolV1.WriteFrameAsync(pipe, ModuleWorkerFrameV1.From("capabilities", capabilities));
                await ServeAsync(pipe, options.Manifest, module);
                await module.StopAsync(CancellationToken.None);
            }
            finally { context.Unload(); }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Module host failed: {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }

    private static async Task ServeAsync(Stream pipe, ModuleManifest manifest, IModule module)
    {
        var active = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var writes = new SemaphoreSlim(1, 1);
        var tasks = new List<Task>();
        try
        {
            while (true)
            {
                var frame = await ModuleWorkerProtocolV1.ReadFrameAsync<ModuleWorkerFrameV1>(pipe);
                if (frame is null) break;
                switch (frame.Kind)
                {
                    case "request":
                    {
                        var request = frame.Decode<ModuleWorkerRequestV1>();
                        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        lock (active)
                        {
                            if (string.IsNullOrWhiteSpace(request.RequestId) || !active.TryAdd(request.RequestId, cancellation))
                                throw new InvalidDataException("Request ID is empty or duplicated.");
                        }
                        tasks.Add(DispatchAsync(pipe, writes, active, request, manifest, module, cancellation));
                        break;
                    }
                    case "cancel":
                    {
                        var cancel = frame.Decode<ModuleWorkerCancelV1>();
                        if (active.TryGetValue(cancel.RequestId, out var cancellation)) cancellation.Cancel();
                        break;
                    }
                    case "health":
                    {
                        var request = frame.Decode<ModuleWorkerHealthRequestV1>();
                        var healthy = module is not IModuleHealthCheck check || await check.IsHealthyAsync(CancellationToken.None);
                        await WriteAsync(pipe, writes, ModuleWorkerFrameV1.From("health", new ModuleWorkerHealthResponseV1(request.CorrelationId, healthy, null)));
                        break;
                    }
                    case "stop": return;
                    default: throw new InvalidDataException($"Unsupported protocol frame kind '{frame.Kind}'.");
                }
                tasks.RemoveAll(task => task.IsCompleted);
            }
        }
        finally
        {
            lock (active) foreach (var cancellation in active.Values) cancellation.Cancel();
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            writes.Dispose();
        }
    }

    private static async Task DispatchAsync(Stream pipe, SemaphoreSlim writes, Dictionary<string, CancellationTokenSource> active,
        ModuleWorkerRequestV1 request, ModuleManifest manifest, IModule module, CancellationTokenSource cancellation)
    {
        ModuleWorkerResponseV1 response;
        try
        {
            if (!manifest.ContractTypes.TryGetValue($"{request.ContractId}@{request.ContractVersion}", out var contractTypeName) ||
                !manifest.Provides.Contains($"{request.ContractId}@{request.ContractVersion}", StringComparer.Ordinal))
                throw new InvalidDataException("Requested capability is not declared by the package.");
            var contract = module.GetType().GetInterfaces().SingleOrDefault(type => string.Equals(type.FullName, contractTypeName, StringComparison.Ordinal))
                ?? throw new InvalidDataException("Declared public contract is not implemented by the module.");
            var methods = contract.GetMethods().Where(method => string.Equals(method.Name, request.Operation, StringComparison.Ordinal)).ToArray();
            if (methods.Length != 1) throw new InvalidDataException("Operation is missing or ambiguous on the public contract.");
            var method = methods[0];
            var parameters = method.GetParameters();
            var operationParameters = parameters.Select((parameter, index) => (parameter, index)).Where(item => item.parameter.ParameterType != typeof(CancellationToken)).ToArray();
            if (operationParameters.Length > 1) throw new InvalidDataException("V1 dispatch supports at most one public operation argument.");
            var values = new object?[parameters.Length];
            foreach (var (parameter, index) in parameters.Select((parameter, index) => (parameter, index)))
                values[index] = parameter.ParameterType == typeof(CancellationToken)
                    ? cancellation.Token
                    : request.Payload.Deserialize(parameter.ParameterType, JsonOptions);
            cancellation.Token.ThrowIfCancellationRequested();
            var result = method.Invoke(module, values);
            result = await AwaitResultAsync(result, method.ReturnType);
            var payload = JsonSerializer.SerializeToElement(result, method.ReturnType == typeof(void) ? typeof(object) : UnwrapTaskType(method.ReturnType), JsonOptions);
            response = new(request.RequestId, true, payload, null);
        }
        catch (OperationCanceledException) { response = new(request.RequestId, false, null, new("cancelled", "Operation was cancelled.", false)); }
        catch (Exception exception)
        {
            var error = exception is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : exception;
            response = new(request.RequestId, false, null, new("module_error", error.Message, false));
        }
        finally
        {
            lock (active) active.Remove(request.RequestId);
            cancellation.Dispose();
        }
        try { await WriteAsync(pipe, writes, ModuleWorkerFrameV1.From("response", response)); } catch (IOException) { }
    }

    private static async Task<object?> AwaitResultAsync(object? result, Type returnType)
    {
        if (returnType == typeof(void)) return null;
        if (result is Task task) { await task; return returnType.IsGenericType ? returnType.GetProperty("Result")!.GetValue(result) : null; }
        if (returnType == typeof(ValueTask)) { await (ValueTask)result!; return null; }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)returnType.GetMethod("AsTask")!.Invoke(result, null)!;
            await asTask;
            return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
        }
        return result;
    }

    private static Type UnwrapTaskType(Type type) => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)) ? type.GetGenericArguments()[0] : type;

    private static async Task WriteAsync(Stream stream, SemaphoreSlim writes, ModuleWorkerFrameV1 frame)
    {
        await writes.WaitAsync();
        try { await ModuleWorkerProtocolV1.WriteFrameAsync(stream, frame); }
        finally { writes.Release(); }
    }

    private static (ModuleLoadContext Context, IModule Module) LoadModule(ModuleManifest manifest, string directory)
    {
        ValidatePackage(manifest, directory);
        var entry = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly!));
        var context = new ModuleLoadContext(entry, manifest.SharedAssemblies);
        try
        {
            var type = context.LoadFromAssemblyPath(entry).GetType(manifest.EntryType!, true, false)!;
            if (!typeof(IModule).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("Entry type must be a public IModule with a parameterless constructor.");
            var module = (IModule)Activator.CreateInstance(type)!;
            if (module.Descriptor.Id != manifest.Id || module.Descriptor.Name != manifest.Name || module.Descriptor.Version != Version.Parse(manifest.Version))
                throw new InvalidDataException("Module identity does not match its manifest.");
            var declared = manifest.Provides.Select(ParseCapability).ToHashSet();
            if (!declared.SetEquals(module.Descriptor.Provides.Select(item => (item.ContractId, item.Version))))
                throw new InvalidDataException("Module capabilities do not match the manifest.");
            var declaredRequires = manifest.Requires.Select(ParseCapability).ToHashSet();
            if (!declaredRequires.SetEquals(module.Descriptor.Requires.Select(item => (item.ContractId, item.Version))))
                throw new InvalidDataException("Module required capabilities do not match the manifest.");
            if (!new HashSet<string>(manifest.Dependencies, StringComparer.Ordinal).SetEquals(module.Descriptor.Dependencies.Select(item => item.ModuleId)))
                throw new InvalidDataException("Module dependencies do not match the manifest.");
            return (context, module);
        }
        catch { context.Unload(); throw; }
    }

    private static void ValidatePackage(ModuleManifest manifest, string directory)
    {
        if (manifest.Runtime != "dotnet-outofprocess" || manifest.PackageFormatVersion != 1 || manifest.Files is null || manifest.Provides is null || manifest.Requires is null || manifest.Dependencies is null || manifest.ContractTypes is null)
            throw new InvalidDataException("Package runtime or format is not supported by the generic host.");
        if (!Version.TryParse(manifest.Version, out _) || !Version.TryParse(manifest.MinimumHostVersion, out var minimum) || new Version(1, 0, 0) < minimum ||
            manifest.MaximumHostVersion is not null && (!Version.TryParse(manifest.MaximumHostVersion, out var maximum) || new Version(1, 0, 0) > maximum))
            throw new InvalidDataException("Package is incompatible with the generic module host version.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType) || !manifest.Files.ContainsKey(manifest.EntryAssembly) ||
            manifest.Provides.Any(capability => !manifest.ContractTypes.ContainsKey(capability)))
            throw new InvalidDataException("Package entry point or public contract type map is incomplete.");
        foreach (var (relative, hash) in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') || relative.Split('/').Any(part => part is "" or "." or "..") || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Unsafe package path or digest.");
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            if (!full.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escaped its directory.");
            if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package file is missing or is a reparse point.");
            var actual = SHA256.HashData(File.ReadAllBytes(full));
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash))) throw new InvalidDataException($"Package integrity check failed: {relative}");
        }
    }

    private static (string, Version) ParseCapability(string capability)
    {
        var at = capability.LastIndexOf('@');
        return at > 0 && Version.TryParse(capability[(at + 1)..], out var version) ? (capability[..at], version) : throw new InvalidDataException("Invalid manifest capability.");
    }

    private static Options Parse(string[] args)
    {
        string? packageDirectory = null, pipe = null, token = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Host arguments require values.");
            switch (args[i])
            {
                case "--package": packageDirectory = Path.GetFullPath(args[++i]); break;
                case "--pipe": pipe = args[++i]; break;
                case "--token-env": token = Environment.GetEnvironmentVariable(args[++i]); break;
                default: throw new ArgumentException($"Unknown host argument '{args[i]}'.");
            }
        }
        if (packageDirectory is null || pipe is null || token is null) throw new ArgumentException("Required arguments: --package, --pipe and --token.");
        var manifestPath = Path.Combine(packageDirectory, "module.json");
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath), JsonOptions) ?? throw new InvalidDataException("Package manifest is empty.");
        return new(packageDirectory, pipe, token, manifest);
    }

    private sealed record Options(string PackageDirectory, string PipeName, string Token, ModuleManifest Manifest);

    private sealed class ModuleLoadContext(string entryPath, IEnumerable<string> shared) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(entryPath);
        private readonly string packageDirectory = Path.GetDirectoryName(entryPath)!;
        private readonly HashSet<string> sharedNames = new(shared, StringComparer.OrdinalIgnoreCase) { typeof(IModule).Assembly.GetName().Name! };
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not null && sharedNames.Contains(name.Name))
                return Default.LoadFromAssemblyName(name);
            var path = resolver.ResolveAssemblyToPath(name) ?? (name.Name is null ? null : Path.Combine(packageDirectory, name.Name + ".dll"));
            if (path is not null && !File.Exists(path)) path = null;
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
