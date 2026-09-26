using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hispania.Server;

public static class WebUiStaticAssets
{
    public static void Configure(IWebHostEnvironment environment, string build = "modern")
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (build is not ("modern" or "classic")) throw new ArgumentException("Web build must be 'modern' or 'classic'.", nameof(build));
        var webRootPath = ResolveWebRootPath(environment.ContentRootPath, environment.WebRootPath, environment.IsDevelopment(), build);
        if (webRootPath is null)
        {
            return;
        }

        environment.WebRootPath = webRootPath;
        environment.WebRootFileProvider = new PhysicalFileProvider(webRootPath);
    }

    private static string? ResolveWebRootPath(string contentRootPath, string? defaultWebRootPath, bool preferDevelopmentBuild, string build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var developmentBuildPath = Path.GetFullPath(Path.Combine(contentRootPath, "..", "web", "dist", build));
        if (preferDevelopmentBuild && Directory.Exists(developmentBuildPath))
        {
            return developmentBuildPath;
        }

        if (!string.IsNullOrWhiteSpace(defaultWebRootPath) && Directory.Exists(defaultWebRootPath))
        {
            return defaultWebRootPath;
        }

        return Directory.Exists(developmentBuildPath) ? developmentBuildPath : defaultWebRootPath;
    }
}
