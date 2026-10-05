using Hispania.Inventory.Application;
using Hispania.Inventory.Contracts;
using Hispania.Platform.Contracts.Assets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace Hispania.Inventory.Persistence.Sqlite;

public static class InventorySqliteRegistration
{
    public static IServiceCollection AddInventorySqlite(this IServiceCollection services, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        services.AddSingleton<IInventoryStore>(_ => new InventorySqliteStore(path));
        services.AddSingleton<InventoryService>();
        services.AddSingleton<IInventoryQueryV1>(provider => provider.GetRequiredService<InventoryService>());
        services.AddSingleton<IInventoryCommandV1>(provider => provider.GetRequiredService<InventoryService>());
        return services;
    }
}

public sealed class InventorySqliteStore : IInventoryStore
{
    private readonly string connectionString;
    public InventorySqliteStore(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        Hispania.Persistence.Schema.SqliteSchemaUpgrade.Initialize(connection, "Inventory.Persistence", """

            CREATE TABLE IF NOT EXISTS items(id TEXT PRIMARY KEY, sku TEXT NOT NULL UNIQUE, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS item_assets(item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE, asset_id TEXT NOT NULL, PRIMARY KEY(item_id,asset_id));
            CREATE TABLE IF NOT EXISTS warehouses(id TEXT PRIMARY KEY, code TEXT NOT NULL UNIQUE, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS locations(id TEXT PRIMARY KEY, warehouse_id TEXT NOT NULL REFERENCES warehouses(id), code TEXT NOT NULL, name TEXT NOT NULL, UNIQUE(warehouse_id,code));
            CREATE TABLE IF NOT EXISTS stock(item_id TEXT NOT NULL REFERENCES items(id), location_id TEXT NOT NULL REFERENCES locations(id), quantity INTEGER NOT NULL CHECK(quantity>=0), PRIMARY KEY(item_id,location_id));
            CREATE TABLE IF NOT EXISTS movements(id TEXT PRIMARY KEY, item_id TEXT NOT NULL REFERENCES items(id), kind INTEGER NOT NULL, quantity INTEGER NOT NULL CHECK(quantity>0), source_id TEXT NULL REFERENCES locations(id), destination_id TEXT NULL REFERENCES locations(id), timestamp TEXT NOT NULL, actor_id TEXT NULL, correlation_id TEXT NULL);
            """);
    }

    public async ValueTask<IReadOnlyList<InventoryItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<InventoryItem>();
        await using var c = await OpenAsync(cancellationToken); await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT id,sku,name FROM items ORDER BY sku";
        var rows = new List<(ItemId Id, string Sku, string Name)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rows.Add((new ItemId(Guid.Parse(reader.GetString(0))), reader.GetString(1), reader.GetString(2)));
        foreach (var row in rows) items.Add(new(row.Id, row.Sku, row.Name, []));
        for (var i = 0; i < items.Count; i++) items[i] = items[i] with { Assets = await GetAssetsAsync(c, items[i].Id, cancellationToken) };
        return items;
    }
    public async ValueTask<InventoryItem?> GetItemAsync(ItemId id, CancellationToken cancellationToken = default) => (await GetItemsAsync(cancellationToken)).FirstOrDefault(x => x.Id == id);
    public async ValueTask<IReadOnlyList<Warehouse>> GetWarehousesAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Warehouse>(); await using var c = await OpenAsync(cancellationToken); await using var cmd = c.CreateCommand(); cmd.CommandText="SELECT id,code,name FROM warehouses ORDER BY code"; await using var r=await cmd.ExecuteReaderAsync(cancellationToken); while(await r.ReadAsync(cancellationToken)) list.Add(new(new(Guid.Parse(r.GetString(0))),r.GetString(1),r.GetString(2))); return list;
    }
    public async ValueTask<IReadOnlyList<Location>> GetLocationsAsync(WarehouseId? warehouseId = null, CancellationToken cancellationToken = default)
    {
        var list=new List<Location>(); await using var c=await OpenAsync(cancellationToken); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT id,warehouse_id,code,name FROM locations WHERE $wid IS NULL OR warehouse_id=$wid ORDER BY code"; cmd.Parameters.AddWithValue("$wid", warehouseId?.Value.ToString("D") ?? (object)DBNull.Value); await using var r=await cmd.ExecuteReaderAsync(cancellationToken); while(await r.ReadAsync(cancellationToken)) list.Add(new(new(Guid.Parse(r.GetString(0))),new(Guid.Parse(r.GetString(1))),r.GetString(2),r.GetString(3))); return list;
    }
    public async ValueTask<IReadOnlyList<StockLevel>> GetStockAsync(ItemId? itemId = null, CancellationToken cancellationToken = default)
    {
        var list=new List<StockLevel>(); await using var c=await OpenAsync(cancellationToken); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT item_id,location_id,quantity FROM stock WHERE $iid IS NULL OR item_id=$iid ORDER BY location_id"; cmd.Parameters.AddWithValue("$iid", itemId?.Value.ToString("D") ?? (object)DBNull.Value); await using var r=await cmd.ExecuteReaderAsync(cancellationToken); while(await r.ReadAsync(cancellationToken)) list.Add(new(new(Guid.Parse(r.GetString(0))),new(Guid.Parse(r.GetString(1))),r.GetInt32(2))); return list;
    }
    public async ValueTask<IReadOnlyList<StockMovement>> GetMovementsAsync(ItemId itemId, CancellationToken cancellationToken = default)
    {
        var list=new List<StockMovement>(); await using var c=await OpenAsync(cancellationToken); await using var cmd=c.CreateCommand(); cmd.CommandText="SELECT id,item_id,kind,quantity,source_id,destination_id,timestamp,actor_id,correlation_id FROM movements WHERE item_id=$iid ORDER BY timestamp,id"; cmd.Parameters.AddWithValue("$iid", itemId.Value.ToString("D")); await using var r=await cmd.ExecuteReaderAsync(cancellationToken); while(await r.ReadAsync(cancellationToken)) list.Add(ReadMovement(r)); return list;
    }
    public async ValueTask SaveItemAsync(InventoryItem item, CancellationToken cancellationToken)
    {
        await using var c=await OpenAsync(cancellationToken); await using var tx=c.BeginTransaction(); await using(var cmd=c.CreateCommand()){cmd.Transaction=tx; cmd.CommandText="INSERT INTO items(id,sku,name) VALUES($id,$sku,$name) ON CONFLICT(id) DO UPDATE SET sku=excluded.sku,name=excluded.name"; cmd.Parameters.AddWithValue("$id",item.Id.Value.ToString("D"));cmd.Parameters.AddWithValue("$sku",item.Sku);cmd.Parameters.AddWithValue("$name",item.Name);await cmd.ExecuteNonQueryAsync(cancellationToken);} await using(var del=c.CreateCommand()){del.Transaction=tx;del.CommandText="DELETE FROM item_assets WHERE item_id=$id";del.Parameters.AddWithValue("$id",item.Id.Value.ToString("D"));await del.ExecuteNonQueryAsync(cancellationToken);} foreach(var asset in item.Assets) await using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="INSERT INTO item_assets(item_id,asset_id) VALUES($iid,$aid)";cmd.Parameters.AddWithValue("$iid",item.Id.Value.ToString("D"));cmd.Parameters.AddWithValue("$aid",asset.Id.Value.ToString("D"));await cmd.ExecuteNonQueryAsync(cancellationToken);} tx.Commit();
    }
    public ValueTask SaveWarehouseAsync(Warehouse warehouse, CancellationToken cancellationToken) => ExecuteInsertAsync("INSERT INTO warehouses(id,code,name) VALUES($id,$code,$name)", cancellationToken, ("$id",warehouse.Id.Value.ToString("D")),("$code",warehouse.Code),("$name",warehouse.Name));
    public ValueTask SaveLocationAsync(Location location, CancellationToken cancellationToken) => ExecuteInsertAsync("INSERT INTO locations(id,warehouse_id,code,name) VALUES($id,$wid,$code,$name)", cancellationToken,("$id",location.Id.Value.ToString("D")),("$wid",location.WarehouseId.Value.ToString("D")),("$code",location.Code),("$name",location.Name));
    public async ValueTask ApplyMovementAsync(StockMovement movement, CancellationToken cancellationToken)
    {
        await using var c=await OpenAsync(cancellationToken); await using var tx=c.BeginTransaction();
        if(movement.SourceLocationId is { } from) await AdjustAsync(c,tx,movement.ItemId,from,-movement.Quantity,cancellationToken);
        if(movement.DestinationLocationId is { } to) await AdjustAsync(c,tx,movement.ItemId,to,movement.Quantity,cancellationToken);
        await using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO movements(id,item_id,kind,quantity,source_id,destination_id,timestamp,actor_id,correlation_id) VALUES($id,$iid,$kind,$q,$src,$dst,$ts,$actor,$corr)";cmd.Parameters.AddWithValue("$id",movement.Id.Value.ToString("D"));cmd.Parameters.AddWithValue("$iid",movement.ItemId.Value.ToString("D"));cmd.Parameters.AddWithValue("$kind",(int)movement.Kind);cmd.Parameters.AddWithValue("$q",movement.Quantity);cmd.Parameters.AddWithValue("$src",movement.SourceLocationId?.Value.ToString("D")??(object)DBNull.Value);cmd.Parameters.AddWithValue("$dst",movement.DestinationLocationId?.Value.ToString("D")??(object)DBNull.Value);cmd.Parameters.AddWithValue("$ts",movement.Timestamp.ToString("O"));cmd.Parameters.AddWithValue("$actor",movement.ActorId??(object)DBNull.Value);cmd.Parameters.AddWithValue("$corr",movement.CorrelationId??(object)DBNull.Value);await cmd.ExecuteNonQueryAsync(cancellationToken);tx.Commit();
    }
    private static async ValueTask AdjustAsync(SqliteConnection c,SqliteTransaction tx,ItemId item,LocationId location,int delta,CancellationToken token)
    {
        await using var cmd=c.CreateCommand();
        cmd.Transaction=tx;
        cmd.Parameters.AddWithValue("$iid",item.Value.ToString("D"));
        cmd.Parameters.AddWithValue("$lid",location.Value.ToString("D"));
        cmd.Parameters.AddWithValue("$delta",delta);
        cmd.CommandText = delta < 0
            ? "UPDATE stock SET quantity=quantity+$delta WHERE item_id=$iid AND location_id=$lid AND quantity+$delta>=0"
            : "INSERT INTO stock(item_id,location_id,quantity) VALUES($iid,$lid,$delta) ON CONFLICT(item_id,location_id) DO UPDATE SET quantity=quantity+$delta";
        if(await cmd.ExecuteNonQueryAsync(token)==0)throw new InvalidOperationException("Insufficient stock.");
    }
    private static async ValueTask<IReadOnlyList<AssetReference>> GetAssetsAsync(SqliteConnection c,ItemId item,CancellationToken token){var assets=new List<AssetReference>();await using var cmd=c.CreateCommand();cmd.CommandText="SELECT asset_id FROM item_assets WHERE item_id=$id ORDER BY asset_id";cmd.Parameters.AddWithValue("$id",item.Value.ToString("D"));await using var r=await cmd.ExecuteReaderAsync(token);while(await r.ReadAsync(token))assets.Add(new AssetReference(new AssetId(Guid.Parse(r.GetString(0)))));return assets;}
    private async ValueTask ExecuteInsertAsync(string sql,CancellationToken token,params (string Name,string Value)[] values){await using var c=await OpenAsync(token);await using var cmd=c.CreateCommand();cmd.CommandText=sql;foreach(var (name,value) in values)cmd.Parameters.AddWithValue(name,value);await cmd.ExecuteNonQueryAsync(token);}
    private static StockMovement ReadMovement(SqliteDataReader r)=>new(new(Guid.Parse(r.GetString(0))),new(Guid.Parse(r.GetString(1))),(StockMovementKind)r.GetInt32(2),r.GetInt32(3),r.IsDBNull(4)?null:new LocationId(Guid.Parse(r.GetString(4))),r.IsDBNull(5)?null:new LocationId(Guid.Parse(r.GetString(5))),DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8));
    private SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken token){var c=new SqliteConnection(connectionString);await c.OpenAsync(token);return c;}
}
