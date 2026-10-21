using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class PackageArchiveAdversarialTests
{
    public static TheoryData<string> UnsafePaths => new()
    {
        "../escape.txt", "/absolute.txt", "C:/absolute.txt", "//server/share.txt",
        "safe\\..\\escape.txt", "safe/..\\escape.txt", "../../sentinel.txt", "CON.txt", "COM1/data.txt",
        "COM¹.txt", "file.txt:stream", "trailing./name.txt", "trailing-space /name.txt"
    };

    [Theory]
    [MemberData(nameof(UnsafePaths))]
    public async Task UnsafePathVariantsAreRejectedBeforeInstallation(string unsafePath)
        => await RejectArchiveAsync([("module.json", "{}", 0), (unsafePath, "canary", 0)]);

    [Fact]
    public async Task ExactDuplicateAndWindowsCaseCollisionsAreRejected()
    {
        await RejectArchiveAsync([("module.json", "{}", 0), ("payload.bin", "a", 0), ("payload.bin", "b", 0)]);
        await RejectArchiveAsync([("module.json", "{}", 0), ("Payload.bin", "a", 0), ("payload.BIN", "b", 0)]);
        await RejectArchiveAsync([("module.json", "{}", 0), ("module.json", "{}", 0)]);
    }

    [Fact]
    public async Task FileDirectoryCollisionsAndUnsafeDirectoryEntriesAreRejected()
    {
        await RejectArchiveAsync([("module.json", "{}", 0), ("node", "file", 0), ("node/child.bin", "child", 0)]);
        await RejectArchiveAsync([("module.json", "{}", 0), ("../outside/", "", 0)]);
        await RejectArchiveAsync([("module.json", "{}", 0), ("linked/", "", unchecked((int)0xA0000000))]);
    }

    [Fact]
    public async Task SymlinkEntriesAreRejectedBeforeAnyExtraction()
        => await RejectArchiveAsync([("module.json", "{}", 0), ("link", "outside-canary", unchecked((int)0xA0000000))]);

    [Fact]
    public async Task EntryCountOverLimitIsRejected()
    {
        var items = new List<(string Name, string Content, int Attributes)> { ("module.json", "{}", 0) };
        for (var index = 0; index < 512; index++) items.Add(($"payload/{index}.bin", "x", 0));
        await RejectArchiveAsync(items);
    }

    [Fact]
    public async Task DeclaredExpandedSizeOverLimitIsRejectedWithoutLargeFixture()
    {
        using var fixture = new TempFixture();
        var archive = fixture.Archive([("module.json", "{}", 0), ("payload.bin", "tiny", 0)]);
        PatchUncompressedSize(archive, "payload.bin", 256L * 1024 * 1024 + 1);
        await AssertRejectedByBothAsync(archive, fixture.Root);
    }

    [Fact]
    public async Task DeclaredLengthMismatchIsRejectedAndLeavesNoStagingResidue()
    {
        using var fixture = new TempFixture();
        var archive = fixture.Archive([("module.json", "{}", 0), ("payload.bin", "four", 0)]);
        PatchUncompressedSize(archive, "payload.bin", 3);
        await AssertRejectedByBothAsync(archive, fixture.Root);
    }

    [Fact]
    public async Task TruncatedAndCorruptArchivesAreRejected()
    {
        using var fixture = new TempFixture();
        var archive = fixture.Archive([("module.json", "{}", 0), ("payload.bin", "canary", 0)]);
        var bytes = File.ReadAllBytes(archive);
        File.WriteAllBytes(archive, bytes[..Math.Max(1, bytes.Length / 2)]);
        await AssertRejectedByBothAsync(archive, fixture.Root);
        File.WriteAllBytes(archive, [0x50, 0x4b, 0x03, 0x04, 0xff]);
        await AssertRejectedByBothAsync(archive, fixture.Root);
    }

    [Fact]
    public async Task InvalidManifestAndPayloadInventoryAreRejected()
    {
        await RejectArchiveAsync([("module.json", "{ not-json", 0)]);
        await RejectArchiveAsync([("module.json", "{}", 0), ("unlisted.bin", "payload", 0)]);
    }

    [Fact]
    public async Task HashFailureCleansStagingAndPreservesExistingInstalledSet()
    {
        using var fixture = new TempFixture();
        var root = Path.Combine(fixture.Root, "store");
        var existing = Path.Combine(root, "existing", "1.0.0");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "sentinel.txt"), "existing-package");
        var before = Snapshot(root);
        var payload = "fixture-payload"u8.ToArray();
        var manifest = new ModuleManifest
        {
            Id = "test.adversarial", Name = "Adversarial fixture", Version = "1.0.0", Implements = [],
            MinimumHostVersion = "1.0.0", Runtime = "dotnet-outofprocess", EntryAssembly = "payload.dll", EntryType = "Fixture.Entry",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["payload.dll"] = new string('0', 64) }
        };
        var archive = fixture.Archive([("module.json", JsonSerializer.Serialize(manifest), 0), ("payload.dll", Convert.ToBase64String(payload), 0)]);
        // Replace the textual payload encoding with exact bytes while retaining a tiny deterministic archive.
        RewriteEntry(archive, "payload.dll", payload);
        var installer = new ExtensionPackageInstaller(root, new Version(1, 0, 0), []);
        await using var input = File.OpenRead(archive);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(input));
        Assert.Equal(before, Snapshot(root));
        Assert.Empty(Directory.EnumerateDirectories(root, ".staging-*", SearchOption.TopDirectoryOnly));
        Assert.False(Directory.Exists(Path.Combine(root, manifest.Id)));
    }

    private static async Task RejectArchiveAsync(IEnumerable<(string Name, string Content, int Attributes)> entries)
    {
        using var fixture = new TempFixture();
        var sentinel = Path.Combine(fixture.Root, "sentinel.txt");
        File.WriteAllText(sentinel, "external-canary");
        var sentinelHash = SHA256.HashData(File.ReadAllBytes(sentinel));
        var archive = fixture.Archive(entries);
        await AssertRejectedByBothAsync(archive, fixture.Root);
        Assert.Equal(sentinelHash, SHA256.HashData(File.ReadAllBytes(sentinel)));
    }

    private static async Task AssertRejectedByBothAsync(string archive, string root)
    {
        await AssertRejectedAsync(() => PackageArchiveInspector.InspectAsync(archive));
        var destination = Path.Combine(root, "not-installed");
        var installer = new ExtensionPackageInstaller(destination, new Version(1, 0, 0), []);
        await using var input = File.OpenRead(archive);
        await AssertRejectedAsync(() => installer.InstallAsync(input));
        Assert.False(Directory.Exists(destination));
    }

    private static async Task AssertRejectedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or ArgumentException) { return; }
        Assert.Fail("Expected the package to be rejected by validation.");
    }

    private static string Snapshot(string root) => string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

    private static void PatchUncompressedSize(string path, string fileName, long length)
    {
        var bytes = File.ReadAllBytes(path);
        for (var offset = 0; offset <= bytes.Length - 30; offset++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            if (signature == 0x04034b50)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 26, 2));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
                var name = System.Text.Encoding.UTF8.GetString(bytes, offset + 30, nameLength);
                if (name == fileName) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 22, 4), checked((uint)length));
                offset += 29 + nameLength + extraLength;
            }
            else if (signature == 0x02014b50)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30, 2));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32, 2));
                var name = System.Text.Encoding.UTF8.GetString(bytes, offset + 46, nameLength);
                if (name == fileName) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), checked((uint)length));
                offset += 45 + nameLength + extraLength + commentLength;
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void RewriteEntry(string archivePath, string name, byte[] content)
    {
        var rewritten = archivePath + ".rewritten";
        using (var source = ZipFile.OpenRead(archivePath))
        using (var output = ZipFile.Open(rewritten, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var target = output.CreateEntry(entry.FullName);
                using var input = entry.Open(); using var destination = target.Open();
                if (entry.FullName == name) destination.Write(content); else input.CopyTo(destination);
            }
        }
        File.Move(rewritten, archivePath, overwrite: true);
    }

    private sealed class TempFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hispania-zip-adversarial-" + Guid.NewGuid().ToString("N"));
        public TempFixture() => Directory.CreateDirectory(Root);
        public string Archive(IEnumerable<(string Name, string Content, int Attributes)> entries)
        {
            var path = Path.Combine(Root, "fixture.zip");
            using var output = File.Create(path);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                entry.ExternalAttributes = item.Attributes;
                if (item.Name.EndsWith('/')) continue;
                using var writer = new StreamWriter(entry.Open()); writer.Write(item.Content);
            }
            return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
