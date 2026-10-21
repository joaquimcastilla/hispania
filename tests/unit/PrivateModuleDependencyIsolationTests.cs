using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Hispania.ExtensionDemo.Contracts;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class PrivateModuleDependencyIsolationTests
{
    private const string PublisherId = "slice8.private-dependency.test";
    private const string ModuleV1Id = "hispania.test.private.v1";
    private const string ModuleV2Id = "hispania.test.private.v2";
    private static readonly string[] AllowedModuleIds = [ModuleV1Id, ModuleV2Id];
    private static readonly string[] ExpectedDependencyVersions = ["1.0.0.0", "2.0.0.0"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("dotnet-inprocess-trusted")]
    [InlineData("dotnet-outofprocess")]
    public async Task PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease(string runtime)
    {
        var root = CreateTemporaryRoot();
        var cleanup = new CleanupDiagnostics(root, runtime);
        try
        {
            await ExerciseAndUnloadPackagesAsync(root, runtime, cleanup);
        }
        finally
        {
            TryDelete(cleanup);
        }
    }

    // Keep lease acquisition, module calls and disposal in a synchronous frame
    // so the caller's async state machine does not retain lease-related references.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExerciseAndUnloadPackagesAsync(string root, string runtime, CleanupDiagnostics cleanup)
    {
        using var key = RSA.Create(2048);
        var registry = new ModuleRegistry();
        using var catalog = CreateCatalog(root, registry, key);
        using var packageV1 = CreatePackage(ModuleV1Id, "Hispania.PrivateFixture.ModuleV1.dll", "Hispania.PrivateFixture.ModuleV1.PrivateModule", FixturePath("module-v1"), key, runtime: runtime);
        using var packageV2 = CreatePackage(ModuleV2Id, "Hispania.PrivateFixture.ModuleV2.dll", "Hispania.PrivateFixture.ModuleV2.PrivateModule", FixturePath("module-v2"), key, runtime: runtime);
        await catalog.InstallAsync(packageV1);
        await catalog.InstallAsync(packageV2);
        await catalog.ActivateAsync(ModuleV1Id, CancellationToken.None);
        await catalog.ActivateAsync(ModuleV2Id, CancellationToken.None);

        await ExerciseLeasesAsync(catalog, runtime, cleanup);
        Assert.True(await catalog.UnloadAsync(ModuleV1Id));
        if (cleanup.AlcWeakReferences[0] is { } firstContext) await WaitForCollectionAsync(firstContext);
        Assert.True(await catalog.UnloadAsync(ModuleV2Id));
        if (cleanup.AlcWeakReferences[1] is { } secondContext) await WaitForCollectionAsync(secondContext);
        ProbePackageFileHandles(root);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ExerciseLeasesAsync(ModuleExtensionCatalog catalog, string runtime, CleanupDiagnostics cleanup)
    {
        var first = catalog.Acquire<IExtensionDemoV1>(ModuleV1Id);
        var second = catalog.Acquire<IExtensionDemoV1>(ModuleV2Id);
        Assert.Equal("private-dependency-v1", first.Value.GetMessage());
        Assert.Equal("private-dependency-v2", second.Value.GetMessage());
        if (runtime == "dotnet-inprocess-trusted")
        {
            Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(first.Value.GetType().Assembly));
            Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(second.Value.GetType().Assembly));
            AssertIsolatedDependencyContexts();
            cleanup.AlcWeakReferences.Add(new WeakReference(CaptureContext(first.Value.GetType().Assembly), trackResurrection: true));
            cleanup.AlcWeakReferences.Add(new WeakReference(CaptureContext(second.Value.GetType().Assembly), trackResurrection: true));
        }
        else
        {
            Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == "Hispania.PrivateFixture.Dependency");
            cleanup.AlcWeakReferences.Add(null);
            cleanup.AlcWeakReferences.Add(null);
        }
        first.Dispose();
        second.Dispose();
        first = null!;
        second = null!;
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, "dotnet-inprocess-trusted")]
    [InlineData(true, "dotnet-inprocess-trusted")]
    [InlineData(false, "dotnet-outofprocess")]
    [InlineData(true, "dotnet-outofprocess")]
    public async Task MissingOrIncompatiblePrivateDependencyFailsActivationWithoutRegistration(bool useIncompatibleDependency, string runtime)
    {
        using var key = RSA.Create(2048);
        var root = CreateTemporaryRoot();
        var cleanup = new CleanupDiagnostics(root, runtime)
        {
            Case = useIncompatibleDependency ? "incompatible-dependency" : "missing-dependency"
        };
        try { await VerifyRejectedDependencyAsync(root, key, useIncompatibleDependency, runtime, cleanup); }
        finally { TryDelete(cleanup); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task VerifyRejectedDependencyAsync(string root, RSA key, bool useIncompatibleDependency, string runtime, CleanupDiagnostics cleanup)
    {
        var registry = new ModuleRegistry();
        using var catalog = CreateCatalog(root, registry, key, [ModuleV1Id]);
        using var package = CreatePackage(ModuleV1Id, "Hispania.PrivateFixture.ModuleV1.dll", "Hispania.PrivateFixture.ModuleV1.PrivateModule", FixturePath("module-v1"), key,
            includeDependency: useIncompatibleDependency,
            dependencySource: useIncompatibleDependency ? FixturePath("module-v2") : null,
            runtime: runtime);
        await catalog.InstallAsync(package);
        var failure = await Assert.ThrowsAsync<ModuleStartupException>(() => catalog.ActivateAsync(ModuleV1Id, CancellationToken.None));
        if (runtime == "dotnet-inprocess-trusted")
        {
            if (useIncompatibleDependency) Assert.IsType<MissingMethodException>(failure.InnerException);
            else Assert.IsAssignableFrom<FileNotFoundException>(failure.InnerException);
        }
        else Assert.NotNull(failure.InnerException);
        Assert.DoesNotContain(registry.List(), module => module.Descriptor.Id == ModuleV1Id);
        Assert.ThrowsAny<Exception>(() => catalog.Acquire<IExtensionDemoV1>(ModuleV1Id));
        cleanup.LoadedFixtureAssemblies = CaptureCandidateContexts(root);
        cleanup.AlcWeakReferences.AddRange(cleanup.LoadedFixtureAssemblies.Select(snapshot => snapshot.AlcReference));
        // Keep this probe separate from production behavior: an activation exception
        // may retain stack metadata from the collectible module until callers release it.
        failure = null!;
        foreach (var snapshot in cleanup.LoadedFixtureAssemblies)
            await WaitForCollectionAsync(snapshot.AlcReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertIsolatedDependencyContexts()
    {
        var privateAssemblies = AssemblyLoadContext.All
            .Where(context => context != AssemblyLoadContext.Default)
            .SelectMany(context => context.Assemblies.Select(assembly => (context, assembly)))
            .Where(item => item.assembly.GetName().Name == "Hispania.PrivateFixture.Dependency")
            .ToArray();
        Assert.Equal(2, privateAssemblies.Length);
        Assert.Equal(ExpectedDependencyVersions, privateAssemblies.Select(item => item.assembly.GetName().Version!.ToString()).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == "Hispania.PrivateFixture.Dependency");
        Assert.NotSame(privateAssemblies[0].context, privateAssemblies[1].context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AssemblyLoadContext CaptureContext(Assembly assembly) => AssemblyLoadContext.GetLoadContext(assembly)!;

    private static ModuleExtensionCatalog CreateCatalog(string root, ModuleRegistry registry, RSA key, IEnumerable<string>? allowlist = null)
    {
        var moduleHost = Path.Combine(RepositoryRoot(), "apps", "module-host", "bin", "Debug", "net10.0", "Hispania.ModuleHost.dll");
        return new ModuleExtensionCatalog(Path.Combine(root, "extensions"), new Version(1, 0, 0), new ModuleRuntime(registry), registry,
            new Dictionary<string, Func<IModule>>(), allowlist ?? AllowedModuleIds, moduleHost,
            new Dictionary<string, string> { [PublisherId] = key.ExportSubjectPublicKeyInfoPem() }, requirePublisherSignature: true);
    }

    private static MemoryStream CreatePackage(string id, string assemblyName, string entryType, string fixtureDirectory, RSA key,
        bool includeDependency = true, string? dependencySource = null, string runtime = "dotnet-inprocess-trusted")
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [assemblyName] = File.ReadAllBytes(Path.Combine(fixtureDirectory, assemblyName)),
            [Path.GetFileNameWithoutExtension(assemblyName) + ".deps.json"] = File.ReadAllBytes(Path.Combine(fixtureDirectory, Path.GetFileNameWithoutExtension(assemblyName) + ".deps.json")),
            ["Hispania.ExtensionDemo.Contracts.dll"] = File.ReadAllBytes(typeof(IExtensionDemoV1).Assembly.Location)
        };
        if (includeDependency)
            payloads["Hispania.PrivateFixture.Dependency.dll"] = File.ReadAllBytes(Path.Combine(dependencySource ?? fixtureDirectory, "Hispania.PrivateFixture.Dependency.dll"));

        var manifest = new ModuleManifest
        {
            Id = id,
            Name = id == ModuleV1Id ? "Private dependency fixture v1" : "Private dependency fixture v2",
            Version = "1.0.0",
            Implements = ["hispania.test.private@1.0"],
            Provides = ["hispania.test.private@1.0"],
            MinimumHostVersion = "1.0.0",
            Runtime = runtime,
            EntryAssembly = assemblyName,
            EntryType = entryType,
            SharedAssemblies = runtime == "dotnet-inprocess-trusted" ? ["Hispania.ExtensionDemo.Contracts"] : [],
            ContractTypes = runtime == "dotnet-outofprocess"
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["hispania.test.private@1.0"] = "Hispania.ExtensionDemo.Contracts.IExtensionDemoV1" }
                : new Dictionary<string, string>(StringComparer.Ordinal),
            Files = payloads.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value)), StringComparer.Ordinal)
        };
        var signed = ModulePackageSignature.Sign(manifest, PublisherId, key.ExportPkcs8PrivateKeyPem());
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifestStream = archive.CreateEntry("module.json").Open()) JsonSerializer.Serialize(manifestStream, signed, Json);
            foreach (var (name, bytes) in payloads)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        output.Position = 0;
        return output;
    }

    private static string FixturePath(string project) => Path.Combine(RepositoryRoot(), "tests", "private-module-fixtures", project, "bin", Configuration(), "net10.0");
    private static string Configuration() => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Hispania.sln"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "hispania-slice8-private-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task WaitForCollectionAsync(WeakReference reference)
    {
        for (var attempt = 0; reference.IsAlive && attempt < 30; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }
        Assert.False(reference.IsAlive, "Collectible module load context remained alive after module unload and lease release.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbePackageFileHandles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "extensions"), "*.dll", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    private static void TryDelete(CleanupDiagnostics diagnostics)
    {
        var path = diagnostics.Root;
        if (!Directory.Exists(path)) return;
        var beforeDeleteSnapshot = WriteSnapshot(diagnostics, "before-delete", null);
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var snapshot = WriteSnapshot(diagnostics, "delete-failed", exception);
            throw new IOException($"Private dependency test could not clean its owned temporary directory ({exception.GetType().Name}, 0x{exception.HResult:X8}). Before-delete diagnostic: {beforeDeleteSnapshot}. Delete-failed diagnostic: {snapshot}", exception);
        }
        if (Directory.Exists(path)) throw new IOException($"Private dependency test temporary directory remains after cleanup: '{path}'.");
    }

    private static string WriteSnapshot(CleanupDiagnostics diagnostics, string stage, Exception? exception)
    {
        var files = new List<FileSnapshot>();
        var directories = new List<DirectorySnapshot>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(diagnostics.Root, "*", SearchOption.AllDirectories).Prepend(diagnostics.Root))
                directories.Add(new DirectorySnapshot(directory, SafeAttributes(directory), GetAclSnapshot(directory), ProbeDeleteAccess(directory, isDirectory: true, checkDeleteChild: true)));
            foreach (var file in Directory.EnumerateFiles(diagnostics.Root, "*", SearchOption.AllDirectories))
                files.Add(new FileSnapshot(file, SafeAttributes(file), GetAclSnapshot(file), ProbeDeleteAccess(file, isDirectory: false, checkDeleteChild: false), ProbeExclusiveRead(file)));
        }
        catch (Exception inventoryError)
        {
            directories.Add(new DirectorySnapshot("<inventory-failed>", null, null, inventoryError.GetType().Name));
        }

        var moduleHostProcesses = Process.GetProcessesByName("Hispania.ModuleHost").Select(process =>
        {
            using (process) return process.Id;
        }).ToArray();
        var snapshot = new
        {
            stage,
            testCase = diagnostics.Case,
            runtime = diagnostics.Runtime,
            processId = Environment.ProcessId,
            processName = Path.GetFileName(Environment.ProcessPath ?? "unknown"),
            managedThreadId = Environment.CurrentManagedThreadId,
            currentDirectory = Environment.CurrentDirectory,
            root = diagnostics.Root,
            utc = DateTimeOffset.UtcNow,
            alcWeakReferencesAlive = diagnostics.AlcWeakReferences.Select(reference => reference?.IsAlive).ToArray(),
            loadedFixtureAssemblies = diagnostics.LoadedFixtureAssemblies.Select(snapshot => new
            {
                snapshot.AssemblyFile,
                snapshot.ContextName,
                snapshot.IsCollectible,
                alcWeakReferenceAlive = snapshot.AlcReference.IsAlive
            }).ToArray(),
            moduleHostProcessIds = moduleHostProcesses,
            exception = exception is null ? null : new
            {
                type = exception.GetType().FullName,
                hresult = $"0x{exception.HResult:X8}",
                win32CodeFromHResult = exception.HResult & 0xFFFF,
                message = exception.Message,
                stackTrace = exception.StackTrace
            },
            directories,
            files
        };
        var json = JsonSerializer.Serialize(snapshot);
        Console.WriteLine("SLICE8_ALC_DELETE_DIAGNOSTIC " + json);
        return json;
    }

    private static string? SafeAttributes(string path)
    {
        try { return File.GetAttributes(path).ToString(); }
        catch (Exception exception) { return "ERROR:" + exception.GetType().Name; }
    }

    private static string? GetAclSnapshot(string path)
    {
        return OperatingSystem.IsWindows() ? GetWindowsAclSnapshot(path) : "unsupported-platform";
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsAclSnapshot(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
                : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            var identity = WindowsIdentity.GetCurrent();
            var matchingSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (identity.User is not null) matchingSids.Add(identity.User.Value);
            if (identity.Groups is not null)
                foreach (var sid in identity.Groups) matchingSids.Add(sid.Value);
            return JsonSerializer.Serialize(new
            {
                ownerSid = security.GetOwner(typeof(SecurityIdentifier))?.Value,
                relevantRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Where(rule => matchingSids.Contains(((SecurityIdentifier)rule.IdentityReference).Value))
                    .Select(rule => new { sid = ((SecurityIdentifier)rule.IdentityReference).Value, rights = rule.FileSystemRights.ToString(), type = rule.AccessControlType.ToString(), inherited = rule.IsInherited })
                    .ToArray()
            });
        }
        catch (Exception exception) { return "ERROR:" + exception.GetType().Name; }
    }

    private static string ProbeDeleteAccess(string path, bool isDirectory, bool checkDeleteChild)
    {
        if (!OperatingSystem.IsWindows()) return "unsupported-platform";
        var access = checkDeleteChild ? 0x00000040u : 0x00010000u; // FILE_DELETE_CHILD or DELETE
        var flags = isDirectory ? 0x02000000u : 0u; // FILE_FLAG_BACKUP_SEMANTICS
        using var handle = CreateFileW(path, access, 0x00000007u, IntPtr.Zero, 3u, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return "opened";
        var win32 = Marshal.GetLastWin32Error();
        var label = win32 switch { 5 => "ERROR_ACCESS_DENIED", 32 => "ERROR_SHARING_VIOLATION", 2 => "ERROR_FILE_NOT_FOUND", 3 => "ERROR_PATH_NOT_FOUND", _ => "OTHER" };
        var hresult = unchecked((int)(0x80070000u | (uint)win32));
        return $"failed:{label}:{win32}:0x{hresult:X8}";
    }

    private static string ProbeExclusiveRead(string path)
    {
        if (!OperatingSystem.IsWindows()) return "unsupported-platform";
        using var handle = CreateFileW(path, 0x80000000u, 0u, IntPtr.Zero, 3u, 0u, IntPtr.Zero); // GENERIC_READ, no sharing
        if (!handle.IsInvalid) return "opened";
        var win32 = Marshal.GetLastWin32Error();
        var label = win32 switch { 5 => "ERROR_ACCESS_DENIED", 32 => "ERROR_SHARING_VIOLATION", 2 => "ERROR_FILE_NOT_FOUND", 3 => "ERROR_PATH_NOT_FOUND", _ => "OTHER" };
        var hresult = unchecked((int)(0x80070000u | (uint)win32));
        return $"failed:{label}:{win32}:0x{hresult:X8}";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixtureAssemblySnapshot[] CaptureCandidateContexts(string root)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly =>
            {
                try
                {
                    var location = assembly.Location;
                    if (location.Length == 0 || !Path.GetFullPath(location).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return null;
                    var context = AssemblyLoadContext.GetLoadContext(assembly);
                    if (context is null || context == AssemblyLoadContext.Default) return null;
                    return new FixtureAssemblySnapshot(Path.GetFileName(location), context.Name, context.IsCollectible, new WeakReference(context, trackResurrection: true));
                }
                catch { return null; }
            })
            .Where(snapshot => snapshot is not null)
            .Cast<FixtureAssemblySnapshot>()
            .DistinctBy(snapshot => (snapshot.AssemblyFile, snapshot.ContextName))
            .ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    private sealed class CleanupDiagnostics(string root, string runtime)
    {
        public string Root { get; } = root;
        public string Runtime { get; } = runtime;
        public string Case { get; set; } = "private-dependency-unload";
        public List<WeakReference?> AlcWeakReferences { get; } = [];
        public FixtureAssemblySnapshot[] LoadedFixtureAssemblies { get; set; } = [];
    }

    private sealed record FixtureAssemblySnapshot(string AssemblyFile, string? ContextName, bool IsCollectible, WeakReference AlcReference);
    private sealed record FileSnapshot(string Path, string? Attributes, string? RelevantAcl, string DeleteAccessProbe, string ExclusiveReadProbe);
    private sealed record DirectorySnapshot(string Path, string? Attributes, string? RelevantAcl, string DeleteChildAccessProbe);
}
