using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class ServerProcessRestartTests
{
    [Fact]
    public async Task RealServerRestartsKeepInventoryAssetsAuditAndPlayableVideo()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-server-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var libraryRoot = Path.Combine(root, "empty-library"); Directory.CreateDirectory(libraryRoot);
        var serverDirectory = FindServerOutputDirectory();
        var serverDll = Path.Combine(serverDirectory, "Hispania.Server.dll");
        Assert.True(File.Exists(serverDll), $"Server output was not built: {serverDll}");
        var adminToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var shutdownToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var port = ReserveLoopbackPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");
        using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(5) };
        Process? server = null;
        try
        {
            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            var first = await AssertPersistentStateAsync(client, adminToken, expectedStock: 12, expectedMinimumAuditRecords: 5);
            await AssertAdminWritesAndAssociationAsync(client, adminToken, first.ItemId, serverDirectory);
            first = await AssertPersistentStateAsync(client, adminToken, expectedStock: 12, expectedMinimumAuditRecords: 8);
            await StopServerGracefullyAsync(server, client, shutdownToken);
            server.Dispose(); server = null;

            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            var afterOrderlyRestart = await AssertPersistentStateAsync(client, adminToken, expectedStock: 12, expectedMinimumAuditRecords: 8);
            Assert.Equal(first.ItemId, afterOrderlyRestart.ItemId);
            Assert.Equal(first.AssetIds.Order(), afterOrderlyRestart.AssetIds.Order());

            await IssueOneUnitAsync(client, adminToken, afterOrderlyRestart.ItemId, afterOrderlyRestart.LocationId);
            _ = await AssertPersistentStateAsync(client, adminToken, expectedStock: 11, expectedMinimumAuditRecords: 9);
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            server.Dispose(); server = null;

            server = StartServer(serverDll, serverDirectory, root, libraryRoot, adminToken, shutdownToken, port);
            await WaitUntilHealthyAsync(server, client);
            var afterAbruptRestart = await AssertPersistentStateAsync(client, adminToken, expectedStock: 11, expectedMinimumAuditRecords: 9);
            Assert.Equal(first.ItemId, afterAbruptRestart.ItemId);
            Assert.Equal(first.AssetIds.Order(), afterAbruptRestart.AssetIds.Order());
            AssertStoreOwnership(root);
            await StopServerGracefullyAsync(server, client, shutdownToken);
            server.Dispose(); server = null;
        }
        finally
        {
            if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
            server?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(Guid ItemId, Guid LocationId, Guid[] AssetIds)> AssertPersistentStateAsync(HttpClient client, string adminToken, int expectedStock, int expectedMinimumAuditRecords)
    {
        using var itemsDoc = await GetJsonAsync(client, "/api/inventory/items");
        var item = Assert.Single(itemsDoc.RootElement.EnumerateArray(), element => element.GetProperty("sku").GetString() == "BP48");
        var itemId = ReadWrappedId(item.GetProperty("id"));
        var references = item.GetProperty("assets").EnumerateArray().Select(element => ReadWrappedId(element.GetProperty("id"))).ToArray();
        Assert.True(references.Length >= 3);

        using var warehousesDoc = await GetJsonAsync(client, "/api/inventory/warehouses");
        var warehouse = Assert.Single(warehousesDoc.RootElement.EnumerateArray(), element => element.GetProperty("code").GetString() == "MAD");
        Assert.Equal("Madrid Warehouse", warehouse.GetProperty("name").GetString());
        using var locationsDoc = await GetJsonAsync(client, "/api/inventory/locations");
        var location = Assert.Single(locationsDoc.RootElement.EnumerateArray(), element => element.GetProperty("code").GetString() == "A-01");
        var locationId = ReadWrappedId(location.GetProperty("id"));
        Assert.Equal(expectedStock, await GetStockAsync(client, itemId));
        using var movementsDoc = await GetJsonAsync(client, $"/api/inventory/items/{itemId:D}/movements");
        Assert.True(movementsDoc.RootElement.GetArrayLength() >= (expectedStock == 12 ? 1 : 2));

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videoId = Guid.Empty;
        foreach (var assetId in references)
        {
            using var metadata = await GetJsonAsync(client, $"/api/assets/{assetId:D}");
            var type = metadata.RootElement.GetProperty("type").GetString()!;
            types.Add(type);
            using var contentResponse = await client.GetAsync($"/api/assets/{assetId:D}/content");
            contentResponse.EnsureSuccessStatusCode();
            var bytes = await contentResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(metadata.RootElement.GetProperty("size").GetInt64(), bytes.LongLength);
            if (type.Equals("Image", StringComparison.OrdinalIgnoreCase)) Assert.StartsWith("<svg", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            if (type.Equals("Document", StringComparison.OrdinalIgnoreCase)) Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
            if (type.Equals("Video", StringComparison.OrdinalIgnoreCase))
            {
                videoId = assetId;
                Assert.True(bytes.Length > 24);
                Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
            }
        }
        Assert.Contains("Image", types); Assert.Contains("Document", types); Assert.Contains("Video", types);

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/internal/admin/audit?domain=inventory&page=1&pageSize=100");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var auditResponse = await client.SendAsync(auditRequest); auditResponse.EnsureSuccessStatusCode();
        using var auditDoc = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync());
        var auditItems = auditDoc.RootElement.GetProperty("items");
        Assert.True(auditItems.GetArrayLength() >= expectedMinimumAuditRecords);
        Assert.Contains(auditItems.EnumerateArray(), record => record.GetProperty("resourceType").GetString() == "stock-movement");

        using var unauthorizedAudit = await client.GetAsync("/internal/admin/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAudit.StatusCode);
        using var playback = await GetJsonAsync(client, $"/api/media/assets/{videoId:D}/playback");
        Assert.True(playback.RootElement.GetProperty("available").GetBoolean());
        var streamUrl = playback.RootElement.GetProperty("streamUrl").GetString()!;
        using var videoResponse = await client.GetAsync(streamUrl); videoResponse.EnsureSuccessStatusCode();
        Assert.Equal("video/mp4", videoResponse.Content.Headers.ContentType?.MediaType);
        var streamBytes = await videoResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal("ftyp", Encoding.ASCII.GetString(streamBytes, 4, 4));
        return (itemId, locationId, references);
    }

    private static async Task AssertAdminWritesAndAssociationAsync(HttpClient client, string adminToken, Guid itemId, string serverDirectory)
    {
        using var noAuth = await client.PostAsync("/api/assets", new ByteArrayContent([0x25, 0x50]));
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        using var badType = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new ByteArrayContent([1, 2, 3]) };
        badType.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        badType.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        badType.Headers.Add("X-Asset-Type", "Image"); badType.Headers.Add("X-Asset-Domain", "erp.inventory"); badType.Headers.Add("X-Asset-Owner", itemId.ToString("D"));
        using var badTypeResponse = await client.SendAsync(badType); Assert.Equal(HttpStatusCode.BadRequest, badTypeResponse.StatusCode);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new ByteArrayContent(new byte[1024 * 1024 + 1]) };
        oversized.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        oversized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        oversized.Headers.Add("X-Asset-Type", "Document"); oversized.Headers.Add("X-Asset-Domain", "erp.inventory"); oversized.Headers.Add("X-Asset-Owner", itemId.ToString("D"));
        using var oversizedResponse = await client.SendAsync(oversized); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

        var pdf = await File.ReadAllBytesAsync(Path.Combine(serverDirectory, "demo-assets", "bp48-datasheet.pdf"));
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets") { Content = new ByteArrayContent(pdf) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        upload.Headers.Add("X-Asset-Type", "Document"); upload.Headers.Add("X-Asset-Domain", "erp.inventory");
        upload.Headers.Add("X-Asset-Owner", itemId.ToString("D")); upload.Headers.Add("X-Asset-Public", "true");
        using var uploadResponse = await client.SendAsync(upload); Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        using var uploaded = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        var assetId = ReadWrappedId(uploaded.RootElement.GetProperty("id"));

        using var unauthAssociation = await client.PostAsJsonAsync($"/api/inventory/items/{itemId:D}/assets", new { assetId });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthAssociation.StatusCode);
        using var association = new HttpRequestMessage(HttpMethod.Post, $"/api/inventory/items/{itemId:D}/assets") { Content = JsonContent.Create(new { assetId }) };
        association.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var associationResponse = await client.SendAsync(association); Assert.Equal(HttpStatusCode.NoContent, associationResponse.StatusCode);
    }

    private static async Task IssueOneUnitAsync(HttpClient client, string adminToken, Guid itemId, Guid locationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/issue") { Content = JsonContent.Create(new { itemId, locationId, quantity = 1 }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
    }

    private static async Task<int> GetStockAsync(HttpClient client, Guid itemId)
    {
        using var stock = await GetJsonAsync(client, "/api/inventory/stock");
        return stock.RootElement.EnumerateArray().Where(level => ReadWrappedId(level.GetProperty("itemId")) == itemId).Sum(level => level.GetProperty("quantity").GetInt32());
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Guid ReadWrappedId(JsonElement id) => Guid.Parse(id.GetProperty("value").GetString()!);

    private static Process StartServer(string serverDll, string workingDirectory, string root, string libraryRoot, string adminToken, string shutdownToken, int port)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(serverDll);
        start.ArgumentList.Add($"--urls=http://127.0.0.1:{port}");
        start.ArgumentList.Add($"--Persistence:Sqlite:Path={Path.Combine(root, "media", "catalog.sqlite")}");
        start.ArgumentList.Add($"--Inventory:Sqlite:Path={Path.Combine(root, "inventory", "inventory.sqlite")}");
        start.ArgumentList.Add($"--Platform:DataPath={Path.Combine(root, "platform")}");
        start.ArgumentList.Add("--Platform:Assets:MaximumBytes=1048576");
        start.ArgumentList.Add($"--DistributedWorkers:StatePath={Path.Combine(root, "workers", "coordinator.sqlite")}");
        start.ArgumentList.Add($"--DistributedWorkers:TransferPath={Path.Combine(root, "workers", "transfers")}");
        start.ArgumentList.Add($"--Library:Roots:0:Key=demo"); start.ArgumentList.Add($"--Library:Roots:0:Path={libraryRoot}");
        start.ArgumentList.Add("--Ffprobe:ExecutablePath=ffprobe"); start.ArgumentList.Add("--Ffmpeg:ExecutablePath=ffmpeg");
        start.ArgumentList.Add($"--Playback:TranscodeCachePath={Path.Combine(root, "media", "transcodes")}");
        start.ArgumentList.Add($"--PlaybackExperiments:StatePath={Path.Combine(root, "platform", "playback-experiments.json")}");
        start.ArgumentList.Add($"--Modules:AdminToken={adminToken}"); start.ArgumentList.Add($"--Testing:ShutdownToken={shutdownToken}");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Hispania.Server process.");
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitUntilHealthyAsync(Process process, HttpClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45); Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Hispania.Server exited early with code {process.ExitCode}.");
            try { using var response = await client.GetAsync("/healthz"); if (response.IsSuccessStatusCode) return; }
            catch (HttpRequestException exception) { lastError = exception; }
            catch (TaskCanceledException exception) { lastError = exception; }
            await Task.Delay(250);
        }
        throw new TimeoutException("Hispania.Server did not become healthy within 45 seconds.", lastError);
    }

    private static async Task StopServerGracefullyAsync(Process process, HttpClient client, string shutdownToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/shutdown");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shutdownToken);
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindServerOutputDirectory()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(root, "apps", "server", "bin", configuration, "net10.0");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void AssertStoreOwnership(string root)
    {
        var inventory = Path.GetFullPath(Path.Combine(root, "inventory", "inventory.sqlite"));
        var assets = Path.GetFullPath(Path.Combine(root, "platform", "assets.sqlite"));
        var audit = Path.GetFullPath(Path.Combine(root, "platform", "audit.sqlite"));
        var media = Path.GetFullPath(Path.Combine(root, "media", "catalog.sqlite"));
        Assert.True(File.Exists(inventory)); Assert.True(File.Exists(assets)); Assert.True(File.Exists(audit)); Assert.True(File.Exists(media));
        Assert.NotEqual(inventory, assets); Assert.NotEqual(assets, audit); Assert.NotEqual(audit, media);
        Assert.True(Directory.EnumerateFiles(Path.Combine(root, "platform", "assets"), "*.blob").Any());
        Assert.False(Directory.EnumerateFiles(Path.Combine(root, "inventory"), "*.blob").Any());
    }
}
