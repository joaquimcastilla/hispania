using System.Reflection;
using System.Runtime.Loader;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

/// <summary>Collectible loader for explicitly authorized in-process extensions. This is not a security sandbox.</summary>
internal sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;
    private readonly HashSet<string> sharedAssemblies;

    public ExtensionAssemblyLoadContext(string entryAssemblyPath, IEnumerable<string> sharedAssemblies)
        : base($"hispania-extension:{Path.GetFileName(Path.GetDirectoryName(entryAssemblyPath))}:{Guid.NewGuid():N}", isCollectible: true)
    {
        resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        this.sharedAssemblies = new HashSet<string>(sharedAssemblies, StringComparer.OrdinalIgnoreCase)
        {
            typeof(IModule).Assembly.GetName().Name!
        };
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && (sharedAssemblies.Contains(assemblyName.Name) || assemblyName.Name.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase)))
        {
            var shared = Default.LoadFromAssemblyName(assemblyName);
            if (!string.Equals(shared.FullName, assemblyName.FullName, StringComparison.OrdinalIgnoreCase))
                throw new FileLoadException($"Extension requested contract identity '{assemblyName.FullName}', but host provides '{shared.FullName}'.");
            return shared;
        }
        var path = resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
