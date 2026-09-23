using Hispania.DevLauncher;
using System.Diagnostics;
using Xunit;

namespace Hispania.DevLauncher.Tests;

public sealed class LauncherTests
{
    [Fact]
    public void FindsRepositoryRootFromNestedDirectory()
    {
        var root = ProjectLocator.FindRoot(Path.Combine(Environment.CurrentDirectory, "nested", "path"));
        Assert.NotNull(root);
        Assert.True(File.Exists(Path.Combine(root!, "Hispania.sln")));
    }

    [Fact]
    public void RejectsDirectoryWithoutRequiredProjectShape()
    {
        var directory = Directory.CreateTempSubdirectory();
        try { Assert.Null(ProjectLocator.FindRoot(directory.FullName)); }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void DefaultConfigDoesNotPersistToken()
    {
        var settings = LauncherSettings.Default;
        var path = LauncherConfig.PathForConfig;
        var old = File.Exists(path) ? File.ReadAllText(path) : null;
        try { LauncherConfig.Save(settings); Assert.DoesNotContain("TmdbToken", File.ReadAllText(path)); }
        finally { if (old is null) File.Delete(path); else File.WriteAllText(path, old); }
    }

    [Fact]
    public async Task StopIsSafeForAProcessThatWasNeverStartedOrWasAlreadyDisposed()
    {
        var owned = new OwnedProcess(new Process());
        await owned.StopAsync();
        owned.Dispose();
        await owned.StopAsync();
        Assert.True(owned.HasExited);
    }

    [Fact]
    public void TmdbTokenIsReadFromTheLauncherTextFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var repository = directory.FullName;
            var tokenPath = LauncherTokenFile.PathFor(repository);
            Assert.Null(LauncherTokenFile.Load(repository));
            Assert.True(File.Exists(tokenPath));
            File.WriteAllText(tokenPath, "  test-token\r\n", new System.Text.UTF8Encoding(false));
            Assert.Equal("test-token", LauncherTokenFile.Load(repository));
        }
        finally { directory.Delete(true); }
    }
}
