using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Persistence.Sqlite;
using Xunit;
using Microsoft.Data.Sqlite;

namespace Hispania.Inventory.Tests;

public sealed class PlatformPersistenceTests
{
    [Fact]
    public async Task AssetsAndAuditSurviveProviderRestartAndAuditSecretsAreFiltered()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-platform-persistence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metadataPath = Path.Combine(root, "assets.sqlite"); var contentPath = Path.Combine(root, "blobs"); var auditPath = Path.Combine(root, "audit.sqlite");
            var firstAssets = new PersistentPlatformAssetStore(new FileAssetContentProvider(contentPath), new SqliteAssetMetadataRepository(metadataPath), 1024 * 1024);
            var expectedBytes = "persistent asset bytes"u8.ToArray();
            await using var source = new MemoryStream(expectedBytes);
            var asset = await firstAssets.StoreAsync(AssetType.Video, "video/mp4", source, new("erp.inventory", "item-1"), new("public-read", true), new Dictionary<string, string> { ["label"] = "demo", ["accessToken"] = "must-not-persist" });
            var firstAudit = new SqlitePlatformAuditStore(auditPath);
            await firstAudit.WriteAsync(new(DateTimeOffset.UtcNow, "admin", "trace-1", "inventory", "asset-associated", "item", "item-1", "success", new Dictionary<string, string> { ["asset"] = asset.Id.Value.ToString(), ["password"] = "must-not-persist" }));

            var restartedAssets = new PersistentPlatformAssetStore(new FileAssetContentProvider(contentPath), new SqliteAssetMetadataRepository(metadataPath), 1024 * 1024);
            var recovered = await restartedAssets.GetMetadataAsync(asset.Id);
            Assert.NotNull(recovered); Assert.Equal(asset.ContentHandle, recovered.ContentHandle); Assert.Equal(AssetStatus.Ready, recovered.Status);
            Assert.False(recovered.Metadata.ContainsKey("accessToken"));
            await using var content = await restartedAssets.OpenReadAsync(asset.Id);
            Assert.NotNull(content); using var buffer = new MemoryStream(); await content.CopyToAsync(buffer); Assert.Equal(expectedBytes, buffer.ToArray());

            var restartedAudit = new SqlitePlatformAuditStore(auditPath);
            var page = await restartedAudit.QueryAsync(new("inventory", ResourceId: "item-1", Page: 1, PageSize: 5));
            var record = Assert.Single(page.Items); Assert.Equal("trace-1", record.CorrelationId); Assert.False(record.Metadata.ContainsKey("password"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
