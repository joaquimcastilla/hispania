using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hispania.Server;

public static class WebUiStaticAssets
{
    public static void Configure(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var webRootPath = ResolveWebRootPath(environment.ContentRootPath, environment.WebRootPath, environment.IsDevelopment());
        if (webRootPath is null)
        {
            return;
        }

        environment.WebRootPath = webRootPath;
        environment.WebRootFileProvider = new PhysicalFileProvider(webRootPath);
    }

    private static string? ResolveWebRootPath(string contentRootPath, string? defaultWebRootPath, bool preferDevelopmentBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var developmentBuildPath = Path.GetFullPath(Path.Combine(contentRootPath, "..", "web", "dist"));
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
