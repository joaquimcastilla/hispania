using Hispania.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class WebUiStaticAssetsTests
{
    [Fact]
    public void UsesViteBuildWhenTheDefaultWebRootDoesNotExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-web-assets", Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(root, "server");
        var build = Path.Combine(root, "web", "dist");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "index.html"), "HISPANIA");

        try
        {
            var environment = new TestWebHostEnvironment(contentRoot, null!);
            WebUiStaticAssets.Configure(environment);
            Assert.Equal(build, environment.WebRootPath);
            Assert.True(environment.WebRootFileProvider.GetFileInfo("index.html").Exists);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PreservesTheExistingPublishedWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-web-assets", Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(root, "server");
        var publishedWebRoot = Path.Combine(contentRoot, "wwwroot");
        Directory.CreateDirectory(publishedWebRoot);

        try
        {
            var environment = new TestWebHostEnvironment(contentRoot, publishedWebRoot);
            WebUiStaticAssets.Configure(environment);
            Assert.Equal(publishedWebRoot, environment.WebRootPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DevelopmentUsesTheCurrentViteBuildInsteadOfAnOlderOutputWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-web-assets", Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(root, "server");
        var publishedWebRoot = Path.Combine(contentRoot, "wwwroot");
        var build = Path.Combine(root, "web", "dist");
        Directory.CreateDirectory(publishedWebRoot); Directory.CreateDirectory(build);
        try
        {
            var environment = new TestWebHostEnvironment(contentRoot, publishedWebRoot) { EnvironmentName = Environments.Development };
            WebUiStaticAssets.Configure(environment);
            Assert.Equal(build, environment.WebRootPath);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath, string webRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Hispania.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = webRootPath;
    }
}
