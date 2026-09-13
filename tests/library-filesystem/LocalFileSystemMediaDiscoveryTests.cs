using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Xunit;

namespace Hispania.Library.LocalFileSystem.Tests;

public sealed class LocalFileSystemMediaDiscoveryTests
{
    [Fact]
    public async Task DiscoversMp4FilesUnderTheConfiguredRootWithoutExposingAbsolutePaths()
    {
        using var directory = TemporaryDirectory.Create();
        directory.WriteFile("movie.mp4", "media");
        directory.WriteFile("nested/feature.MP4", "media");
        directory.WriteFile("nested/ignore.mkv", "media");

        var candidates = await DiscoverAsync(directory.Path);

        Assert.Equal(["movie.mp4", "nested/feature.MP4"], candidates.Select(candidate => candidate.File.RelativePath).Order());
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("movies", candidate.File.RootKey);
            Assert.False(Path.IsPathRooted(candidate.File.RelativePath));
            Assert.NotNull(candidate.ByteLength);
            Assert.NotNull(candidate.LastModifiedUtc);
        });
    }

    [Fact]
    public void DuplicateRootKeysAreRejected()
    {
        using var directory = TemporaryDirectory.Create();

        Assert.Throws<ArgumentException>(() => new LocalLibraryRootConfiguration(
        [
            new LocalLibraryRoot("movies", directory.Path),
            new LocalLibraryRoot("movies", directory.Path)
        ]));
    }

    [Theory]
    [InlineData("../escape.mp4")]
    [InlineData("C:escape.mp4")]
    [InlineData("C:\\escape.mp4")]
    [InlineData("\\\\?\\C:\\escape.mp4")]
    public void CandidatePathResolverRejectsTraversalAndRootedDisguises(string relativePath)
    {
        using var directory = TemporaryDirectory.Create();
        var root = new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory.Path)]).Roots.Single();

        Assert.Throws<ArgumentException>(() => RootPathResolver.ResolveCandidatePath(root, relativePath));
    }

    [Fact]
    public void ContainmentDoesNotConfuseSimilarPathPrefixes()
    {
        using var directory = TemporaryDirectory.Create();
        var rootPath = Path.Combine(directory.Path, "Media");
        var siblingPath = Path.Combine(directory.Path, "Media2", "movie.mp4");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
        File.WriteAllText(siblingPath, "media");
        var root = new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", rootPath)]).Roots.Single();

        Assert.Throws<ArgumentException>(() => RootPathResolver.GetContainedRelativePath(root, siblingPath));
    }

    [Fact]
    public async Task MissingRootFailsClearly()
    {
        using var directory = TemporaryDirectory.Create();
        var discovery = CreateDiscovery(Path.Combine(directory.Path, "missing"));

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await DrainAsync(discovery));

        Assert.Contains("movies", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsEnumeration()
    {
        using var directory = TemporaryDirectory.Create();
        directory.WriteFile("first.mp4", "media");
        directory.WriteFile("second.mp4", "media");
        var discovery = CreateDiscovery(directory.Path);
        using var cancellation = new CancellationTokenSource();
        var observed = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in discovery.DiscoverAsync(cancellation.Token))
            {
                observed++;
                cancellation.Cancel();
            }
        });

        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task ReparsePointsAreSkippedWhetherTheyPointInsideOrOutsideTheRoot()
    {
        using var directory = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        directory.WriteFile("inside.mp4", "media");
        outside.WriteFile("outside.mp4", "media");
        var insideTarget = Path.Combine(directory.Path, "inside-target");
        Directory.CreateDirectory(insideTarget);
        File.WriteAllText(Path.Combine(insideTarget, "inside-link-target.mp4"), "media");

        if (!TryCreateDirectoryLink(Path.Combine(directory.Path, "outside-link"), outside.Path) ||
            !TryCreateDirectoryLink(Path.Combine(directory.Path, "inside-link"), insideTarget))
        {
            return;
        }

        var candidates = await DiscoverAsync(directory.Path);

        var discoveredPaths = candidates.Select(candidate => candidate.File.RelativePath).ToArray();

        Assert.Contains("inside.mp4", discoveredPaths);
        Assert.Contains("inside-target/inside-link-target.mp4", discoveredPaths);
        Assert.DoesNotContain(discoveredPaths, path => path.StartsWith("outside-link/", StringComparison.Ordinal));
        Assert.DoesNotContain(discoveredPaths, path => path.StartsWith("inside-link/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeletedFileDuringDiscoveryIsNotReadOrEmitted()
    {
        using var directory = TemporaryDirectory.Create();
        var deletedFile = directory.WriteFile("deleted.mp4", "media");
        File.Delete(deletedFile);
        directory.WriteFile("remaining.mp4", "media");

        var candidates = await DiscoverAsync(directory.Path);

        Assert.Equal(["remaining.mp4"], candidates.Select(candidate => candidate.File.RelativePath));
    }

    private static LocalFileSystemMediaDiscovery CreateDiscovery(string rootPath) =>
        new(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", rootPath)]));

    private static async Task<IReadOnlyList<DiscoveredMediaCandidate>> DiscoverAsync(string rootPath) =>
        await DrainAsync(CreateDiscovery(rootPath));

    private static async Task<IReadOnlyList<DiscoveredMediaCandidate>> DrainAsync(LocalFileSystemMediaDiscovery discovery)
    {
        var candidates = new List<DiscoveredMediaCandidate>();
        await foreach (var candidate in discovery.DiscoverAsync())
        {
            candidates.Add(candidate);
        }

        return candidates;
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-library-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
