using Hispania.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class Phase21EndpointPolicyTests
{
    [Fact]
    public void ChildPoliciesStripProductionSecretProviderValuesAndPreserveRequiredEnvironment()
    {
        foreach (var policy in new object[] { new Hispania.Runtime.OutOfProcessResourceLimits(), new Hispania.Transcoding.FFmpeg.FfmpegWorkerPolicy() })
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet");
            start.Environment["HISPANIA_SECRET_TEST"] = "test-only-secret";
            start.Environment["hispania_secret_lowercase"] = "test-only-secret";
            start.Environment["HISPANIA_MODULE_LAUNCH_TOKEN"] = "test-only-launch-token";
            start.Environment["HISPANIA_FFMPEG_REQUEST_V1"] = "{}";
            start.Environment["PATH"] = "test-only-path";
            var method = policy.GetType().GetMethod(policy is Hispania.Runtime.OutOfProcessResourceLimits ? "Configure" : "ConfigureStartInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            method.Invoke(policy, [start]);
            Assert.DoesNotContain(start.Environment.Keys, name => name.StartsWith("HISPANIA_SECRET_", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("test-only-launch-token", start.Environment["HISPANIA_MODULE_LAUNCH_TOKEN"]);
            Assert.Equal("{}", start.Environment["HISPANIA_FFMPEG_REQUEST_V1"]);
            Assert.Equal("test-only-path", start.Environment["PATH"]);
        }
    }

    [Fact]
    public async Task ServiceToolingRejectsControlCharacterPathBeforeServiceMutation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Hispania.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh" : "sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root.FullName, "deployment", "windows-service.ps1"), "-Action", "status", "-InstallationHome", "C:\\phase21\nunsafe" }) start.ArgumentList.Add(argument);
        }
        else foreach (var argument in new[] { Path.Combine(root.FullName, "deployment", "install-systemd.sh"), "install", "/tmp/phase21\nunsafe", "server" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(OperatingSystem.IsWindows() ? "control characters" : "Unsupported service path", await output + await error);
    }
    [Fact]
    public async Task UnclassifiedRouteFailsClosedAndPermissionMetadataMatchesFilter()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/accidental-admin", () => "private");
        var routes = ((IEndpointRouteBuilder)app).DataSources;
        Assert.Throws<InvalidOperationException>(() => SecurityEndpointInventory.Validate(routes));
        await using var publicApp = WebApplication.CreateBuilder().Build();
        publicApp.MapGet("/api/health", () => "healthy");
        SecurityEndpointInventory.Validate(((IEndpointRouteBuilder)publicApp).DataSources);
        publicApp.MapPost("/api/health", () => "accidental-public-write");
        Assert.Throws<InvalidOperationException>(() => SecurityEndpointInventory.Validate(((IEndpointRouteBuilder)publicApp).DataSources));
        await using var protectedApp = WebApplication.CreateBuilder().Build();
        protectedApp.MapPost("/protected", () => "private").RequirePlatformPermission("platform.admin", false);
        var snapshot = Assert.Single(SecurityEndpointInventory.Snapshot(((IEndpointRouteBuilder)protectedApp).DataSources));
        Assert.Equal("permission:platform.admin", snapshot.Policy);
        Assert.False(snapshot.LegacyBearer);
        SecurityEndpointInventory.Validate(((IEndpointRouteBuilder)protectedApp).DataSources);
    }
}
