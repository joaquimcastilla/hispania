using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Hispania.Server;

public static class WebUiStaticAssets
{
    public static void Configure(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var webRootPath = ResolveWebRootPath(environment.ContentRootPath, environment.WebRootPath);
        if (webRootPath is null)
        {
            return;
        }

        environment.WebRootPath = webRootPath;
        environment.WebRootFileProvider = new PhysicalFileProvider(webRootPath);
    }

    private static string? ResolveWebRootPath(string contentRootPath, string? defaultWebRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (!string.IsNullOrWhiteSpace(defaultWebRootPath) && Directory.Exists(defaultWebRootPath))
        {
            return defaultWebRootPath;
        }

        var developmentBuildPath = Path.GetFullPath(Path.Combine(contentRootPath, "..", "web", "dist"));
        return Directory.Exists(developmentBuildPath) ? developmentBuildPath : defaultWebRootPath;
    }
}
