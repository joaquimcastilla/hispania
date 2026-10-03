using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Storage;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.Storage.Application;
using Hispania.Platform.Storage.Filesystem;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class StorageProviderTests
{
    [Fact]
    public async Task FilesystemProviderRejectsMalformedAndTraversingHandlesAndStreamsConcurrentObjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FilesystemStorageProviderV1(root);
            foreach (var value in new[] { "../secret", "..\\secret", "C:\\secret", "/etc/passwd", "storage://filesystem/../secret", "storage://filesystem/%2e%2e", "storage://filesystem/not-an-id", "storage://filesystem/" })
            {
                await Assert.ThrowsAsync<ArgumentException>(async () => await provider.OpenReadAsync(new(value)));
                await Assert.ThrowsAsync<ArgumentException>(async () => await provider.DeleteAsync(new(value)));
            }

            var first = await provider.WriteAsync(new MemoryStream([1, 2, 3]), 8);
            var second = await provider.WriteAsync(new MemoryStream([4, 5]), 8);
            Assert.StartsWith("storage://filesystem/", first.Handle.Value);
            Assert.NotEqual(first.Handle, second.Handle);
            await using var opened = await provider.OpenReadAsync(first.Handle);
            Assert.NotNull(opened);
            using var buffer = new MemoryStream();
            await opened!.CopyToAsync(buffer);
            Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
            Assert.Equal(3, first.Metadata.Size);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await provider.WriteAsync(new MemoryStream(new byte[9]), 8));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories));

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => provider.WriteAsync(new MemoryStream([(byte)index]), 8).AsTask()));
            Assert.Equal(12, concurrent.Select(item => item.Handle.Value).Distinct(StringComparer.Ordinal).Count());
            var readRace = provider.OpenReadAsync(second.Handle).AsTask();
            var deleteRace = provider.DeleteAsync(second.Handle).AsTask();
            await Task.WhenAll(readRace, deleteRace);
            var racedStream = await readRace;
            var wasDeleted = await deleteRace;
            if (racedStream is not null) await racedStream.DisposeAsync();
            Assert.True(wasDeleted || racedStream is not null);

            var outside = Path.Combine(Path.GetTempPath(), "hispania-storage-outside-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(outside, "outside");
            try
            {
                var linkPath = Path.Combine(root, "0123456789abcdef0123456789abcdef.blob");
                try
                {
                    File.CreateSymbolicLink(linkPath, outside);
                    await Assert.ThrowsAsync<IOException>(async () => await provider.OpenReadAsync(new StorageObjectHandle("storage://filesystem/0123456789abcdef0123456789abcdef")));
                }
                catch (UnauthorizedAccessException) { }
                catch (PlatformNotSupportedException) { }
                catch (IOException) when (OperatingSystem.IsWindows()) { }
            }
            finally { File.Delete(outside); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExistingSqliteAssetsMigrateProviderIdAndKeepLegacyHexHandlesReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-storage-legacy-" + Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(root, "objects");
        try
        {
            Directory.CreateDirectory(contentRoot);
            var id = Guid.NewGuid();
            const string legacyHandle = "ABCDEF0123456789ABCDEF0123456789";
            await File.WriteAllBytesAsync(Path.Combine(contentRoot, legacyHandle + ".blob"), [9, 8, 7]);
            var databasePath = Path.Combine(root, "assets.sqlite");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE platform_assets(id TEXT PRIMARY KEY,type INTEGER NOT NULL,mime TEXT NOT NULL,size INTEGER NOT NULL,metadata TEXT NOT NULL,scope_domain TEXT NOT NULL,owner_id TEXT NOT NULL,policy_id TEXT NOT NULL,public_read INTEGER NOT NULL,content_handle TEXT NOT NULL,status INTEGER NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL); INSERT INTO platform_assets VALUES($id,4,'application/octet-stream',3,'{}','legacy','owner','owner',0,$handle,0,$created,$created)";
                command.Parameters.AddWithValue("$id", id.ToString("D")); command.Parameters.AddWithValue("$handle", legacyHandle); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteAssetMetadataRepository(databasePath);
            var store = new PersistentPlatformAssetStore(new StorageProviderRegistryV1("filesystem", [new FilesystemStorageProviderV1(contentRoot)]), repository, 1024);
            var migrated = await store.GetMetadataAsync(new AssetId(id));
            Assert.Equal("filesystem", migrated?.ProviderId);
            await using var stream = await store.OpenReadAsync(new AssetId(id));
            Assert.NotNull(stream);
            using var output = new MemoryStream(); await stream!.CopyToAsync(output); Assert.Equal(new byte[] { 9, 8, 7 }, output.ToArray());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RegistryResolvesMultipleProvidersAndRejectsUnknownProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var filesystem = new FilesystemStorageProviderV1(root);
            var memory = new TestStorageProvider();
            var registry = new StorageProviderRegistryV1("filesystem", [filesystem, memory]);
            Assert.Same(filesystem, registry.Resolve(new StorageObjectHandle("storage://filesystem/0123456789abcdef0123456789abcdef")));
            Assert.Same(memory, registry.Resolve(new StorageObjectHandle("storage://memory/object-1")));
            Assert.Throws<StorageProviderNotFoundException>(() => registry.Resolve("missing"));
            Assert.Throws<StorageProviderNotFoundException>(() => registry.Resolve(new StorageObjectHandle("storage://missing/id")));
            Assert.Throws<ArgumentException>(() => registry.Resolve(new StorageObjectHandle("storage://filesystem/%2e%2e")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AssetStoreFailsExplicitlyForUnknownProviderAndReturnsMissingObjectsAsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metadata = new SqliteAssetMetadataRepository(Path.Combine(root, "assets.sqlite"));
            var store = new PersistentPlatformAssetStore(
                new StorageProviderRegistryV1("filesystem", [new FilesystemStorageProviderV1(Path.Combine(root, "objects"))]), metadata, 1024);
            var row = new AssetMetadata(new(Guid.NewGuid()), AssetType.Binary, "application/octet-stream", 0, new Dictionary<string, string>(), new("test", "owner"), new("owner"), "storage://missing/object", ProviderId: "missing");
            await metadata.SaveAsync(row);
            await Assert.ThrowsAsync<StorageProviderNotFoundException>(async () => await store.OpenReadAsync(row.Id));

            var missing = row with { Id = new(Guid.NewGuid()), ContentHandle = "storage://filesystem/0123456789abcdef0123456789abcdef", ProviderId = "filesystem" };
            await metadata.SaveAsync(missing);
            Assert.Null(await store.OpenReadAsync(missing.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class TestStorageProvider : IStorageProviderV1
    {
        public string ProviderId => "memory";
        public ValueTask<(StorageObjectHandle Handle, StorageObjectMetadata Metadata)> WriteAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<Stream?> OpenReadAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream?>(null);
        public ValueTask<bool> DeleteAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<bool> ExistsAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<StorageObjectMetadata?> GetMetadataAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default) => ValueTask.FromResult<StorageObjectMetadata?>(null);
        public ValueTask<StorageProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new StorageProviderHealth(true));
    }
}
