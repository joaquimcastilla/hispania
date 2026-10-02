using Hispania.Inventory.Application;
using Hispania.Inventory.Contracts;
using Hispania.Inventory.Persistence.Sqlite;
using Hispania.Media.Domain.Works;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Metadata.Application;
using Hispania.Playback.Application;
using Hispania.Transcoding.FFmpeg;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Authorization.Application;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class PlatformDependencyTests
{
    [Fact]
    public void PlatformAndInventoryAssembliesHaveNoCrossDomainImplementationReferences()
    {
        AssertNoForbiddenReferences(typeof(IPlatformEvent).Assembly, "Media", "Inventory", "Store");
        AssertNoForbiddenReferences(typeof(IDurablePlatformEventBusV1).Assembly, "Media", "Inventory", "Store");
        AssertNoForbiddenReferences(typeof(SqliteDurablePlatformEventBus).Assembly, "Media", "Inventory", "Store");
        Assert.Contains(typeof(InventoryService).Assembly.GetReferencedAssemblies(), reference => reference.Name == typeof(IDurablePlatformEventBusV1).Assembly.GetName().Name);
        AssertNoForbiddenReferences(typeof(LocalPlatformEventBus).Assembly, "Media", "Inventory", "Store");
        AssertNoForbiddenReferences(typeof(InventoryService).Assembly, "Media", "Playback", "Library", "Metadata", "Store");
        AssertNoForbiddenReferences(typeof(InventoryService).Assembly, "Authorization.Application");
        AssertNoForbiddenReferences(typeof(InventorySqliteStore).Assembly, "Media", "Playback", "Library", "Metadata", "Store");
        AssertNoForbiddenReferences(typeof(DirectPlayPlanner).Assembly, "Authorization.Application");
        AssertNoForbiddenReferences(typeof(LibraryScanner).Assembly, "Authorization.Application");
        AssertNoForbiddenReferences(typeof(AuthorizationService).Assembly, "Media", "Inventory");
        Assert.Equal("inventory.read", InventoryPermissionIds.Read);
        Assert.Equal("inventory.write", InventoryPermissionIds.Write);
        Assert.Equal("media.library.read", MediaPermissionIds.LibraryRead);
        Assert.Equal("media.playback.use", MediaPermissionIds.PlaybackUse);
        Assert.Equal("media.admin", MediaPermissionIds.Admin);
        Assert.DoesNotContain(typeof(Work).Assembly.GetReferencedAssemblies(), reference => reference.Name?.Contains("Inventory", StringComparison.OrdinalIgnoreCase) == true);
        AssertNoInventoryReference(typeof(LibraryScanner).Assembly);
        AssertNoInventoryReference(typeof(MovieMetadataApplicationService).Assembly);
        AssertNoInventoryReference(typeof(DirectPlayPlanner).Assembly);
        AssertNoInventoryReference(typeof(FfmpegCompatibleMediaTranscoder).Assembly);
        AssertNoInventoryReference(typeof(SqliteLibraryPersistence).Assembly);
    }

    [Fact]
    public void WebClientDoesNotImportDomainImplementationProjects()
    {
        var root = FindRepositoryRoot();
        var webSource = Path.Combine(root, "apps", "web", "src");
        var forbiddenImport = new System.Text.RegularExpressions.Regex("from\\s+['\\\"](?:\\.\\./)+(?:core|application|infrastructure|modules)/", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (var file in Directory.EnumerateFiles(webSource, "*.ts*", SearchOption.AllDirectories))
            Assert.DoesNotMatch(forbiddenImport, File.ReadAllText(file));
    }

    [Fact]
    public void WorkerAndPlatformPersistenceStayIndependentOfProductImplementations()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var workerProject = File.ReadAllText(Path.Combine(root, "apps", "worker-agent", "Hispania.WorkerAgent.csproj"));
        Assert.DoesNotContain("Transcoding.FFmpeg", workerProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FFmpeg.Worker", workerProject, StringComparison.OrdinalIgnoreCase);
        var assetsProject = File.ReadAllText(Path.Combine(root, "infrastructure", "platform", "persistence-sqlite", "Hispania.Platform.Persistence.Sqlite.csproj"));
        Assert.DoesNotContain("Media", assetsProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Inventory", assetsProject, StringComparison.OrdinalIgnoreCase);
        var worker = System.Reflection.Assembly.LoadFrom(Path.Combine(root, "apps", "worker-agent", "bin", configuration, "net10.0", "Hispania.WorkerAgent.dll"));
        AssertNoForbiddenReferences(worker, "FFmpeg", "Playback", "Inventory", "Media");
        AssertNoForbiddenReferences(typeof(PersistentPlatformAssetStore).Assembly, "FFmpeg", "Playback", "Inventory", "Media", "Store");
        Assert.DoesNotContain(typeof(IPlatformAuditWriter).GetMethods(), method => method.Name != "WriteAsync");
        Assert.DoesNotContain(typeof(IPlatformAuditReader).GetMethods(), method => method.Name != "QueryAsync");

        var server = System.Reflection.Assembly.LoadFrom(Path.Combine(root, "apps", "server", "bin", configuration, "net10.0", "Hispania.Server.dll"));
        var serverReferences = server.GetReferencedAssemblies();
        Assert.Contains(serverReferences, reference => reference.Name?.Contains("Inventory", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(serverReferences, reference => reference.Name?.Contains("Transcoding.FFmpeg", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(serverReferences, reference => reference.Name?.Contains("Platform.Persistence", StringComparison.OrdinalIgnoreCase) == true);

        using var filter = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Hispania.Platform.slnf")));
        foreach (var project in filter.RootElement.GetProperty("solution").GetProperty("projects").EnumerateArray())
        {
            var path = project.GetString()!;
            Assert.DoesNotContain("inventory", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("playback", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transcoding-ffmpeg", path, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoForbiddenReferences(System.Reflection.Assembly assembly, params string[] forbidden)
    {
        foreach (var reference in assembly.GetReferencedAssemblies())
            Assert.DoesNotContain(forbidden, name => reference.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void AssertNoInventoryReference(System.Reflection.Assembly assembly) =>
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name?.Contains("Inventory", StringComparison.OrdinalIgnoreCase) == true);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
