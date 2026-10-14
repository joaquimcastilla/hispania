using System.IO.Compression;
using System.Security.Cryptography;
using Hispania.DeterministicZip;
using Xunit;

namespace Hispania.DeterministicZip.Tests;

public sealed class DeterministicZipTests
{
    [Fact]
    public void IdenticalTreesWithDifferentFileTimesAndCreationOrderProduceIdenticalZipBytes()
    {
        using var fixture = new Fixture();
        var first = fixture.Directory("first");
        var second = fixture.Directory("second");
        WriteTree(first, ["zeta.txt", "nested/alpha.sh"]);
        WriteTree(second, ["nested/alpha.sh", "zeta.txt"]);
        File.SetLastWriteTimeUtc(Path.Combine(first, "zeta.txt"), new DateTime(2024, 3, 4, 5, 6, 8, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(second, "zeta.txt"), new DateTime(2025, 7, 8, 9, 10, 12, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(first, "nested/alpha.sh"), new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(second, "nested/alpha.sh"), new DateTime(2026, 8, 9, 10, 11, 12, DateTimeKind.Utc));
        var firstZip = Path.Combine(fixture.Root, "first.zip");
        var secondZip = Path.Combine(fixture.Root, "second.zip");

        Program.Create(first, firstZip);
        Program.Create(second, secondZip);

        Assert.Equal(Hash(firstZip), Hash(secondZip));
        Assert.Equal(File.ReadAllBytes(firstZip), File.ReadAllBytes(secondZip));
    }

    [Fact]
    public void EntriesHaveCanonicalOrderTimestampAndUnixModes()
    {
        using var fixture = new Fixture();
        var source = fixture.Directory("tree");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "run.sh"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(source, "plain.txt"), "plain\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(source, "run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var package = Path.Combine(fixture.Root, "tree.zip");
        Program.Create(source, package);

        using var archive = ZipFile.OpenRead(package);
        Assert.Equal(["empty/", "plain.txt", "run.sh"], archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry => Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime));
        Assert.Equal(0x41ED, (int)((uint)archive.GetEntry("empty/")!.ExternalAttributes >> 16));
        Assert.Equal(0x81A4, (int)((uint)archive.GetEntry("plain.txt")!.ExternalAttributes >> 16));
        var expectedMode = OperatingSystem.IsWindows() ? 0x81A4 : 0x81ED;
        Assert.Equal(expectedMode, (int)((uint)archive.GetEntry("run.sh")!.ExternalAttributes >> 16));
        Assert.All(archive.Entries, entry => Assert.Equal(entry.Length, entry.CompressedLength));
        var bytes = File.ReadAllBytes(package);
        var centralDirectory = FindSignature(bytes, 0x02014b50);
        Assert.NotEqual(-1, centralDirectory);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, centralDirectory + 10));
        Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, centralDirectory + 30));
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("../escape")]
    [InlineData("folder/../escape")]
    [InlineData("C:/rooted")]
    [InlineData("folder\\file")]
    [InlineData("CON.txt")]
    public void RejectsUnsafePortableArchivePaths(string path) => Assert.Throws<InvalidOperationException>(() => Program.ValidatePath(path));

    [Fact]
    public void UsesUtf8NamesForNonAsciiPaths()
    {
        using var fixture = new Fixture();
        var source = fixture.Directory("tree");
        File.WriteAllText(Path.Combine(source, "café.txt"), "payload");
        var package = Path.Combine(fixture.Root, "utf8.zip");
        Program.Create(source, package);
        using var archive = ZipFile.OpenRead(package);
        Assert.Equal("café.txt", Assert.Single(archive.Entries).FullName);
    }

    [Fact]
    public void RejectsLinksAndOutputInsideSource()
    {
        using var fixture = new Fixture();
        var source = fixture.Directory("tree");
        Assert.Throws<InvalidOperationException>(() => Program.Create(source, Path.Combine(source, "nested.zip")));
        if (OperatingSystem.IsWindows()) return;
        var target = fixture.File("target", "payload");
        File.CreateSymbolicLink(Path.Combine(source, "linked"), target);
        Assert.Throws<InvalidOperationException>(() => Program.Create(source, Path.Combine(fixture.Root, "link.zip")));
    }

    [Fact]
    public void RejectsCaseCollidingPathsForPortableExtraction()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var source = fixture.Directory("tree");
        File.WriteAllText(Path.Combine(source, "Readme.txt"), "one");
        File.WriteAllText(Path.Combine(source, "README.TXT"), "two");
        Assert.Throws<InvalidOperationException>(() => Program.Create(source, Path.Combine(fixture.Root, "portable.zip")));
    }

    [Fact]
    public void RejectsKnownTemporaryAndEditorResidueFiles()
    {
        using var fixture = new Fixture();
        var source = fixture.Directory("tree");
        File.WriteAllText(Path.Combine(source, "app.tmp"), "unfinished");
        Assert.Throws<InvalidOperationException>(() => Program.Create(source, Path.Combine(fixture.Root, "temp.zip")));
    }

    private static void WriteTree(string root, string[] paths)
    {
        foreach (var path in paths)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, path.EndsWith(".sh", StringComparison.Ordinal) ? "#!/bin/sh\nexit 0\n" : "same payload\n");
            if (path.EndsWith(".sh", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
            if (BitConverter.ToUInt32(bytes, index) == signature) return index;
        return -1;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture() => Root = Path.Combine(Path.GetTempPath(), "hispania-deterministic-zip-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Directory(string name) { var path = Path.Combine(Root, name); System.IO.Directory.CreateDirectory(path); return path; }
        public string File(string name, string contents) { System.IO.Directory.CreateDirectory(Root); var path = Path.Combine(Root, name); System.IO.File.WriteAllText(path, contents); return path; }
        public void Dispose() { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true); }
    }
}
