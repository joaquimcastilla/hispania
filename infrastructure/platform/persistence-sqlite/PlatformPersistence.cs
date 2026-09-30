using System.Text.Json;
using System.Globalization;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Identity;
using Hispania.Identity.Application;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Platform.Persistence.Sqlite;

public static class PlatformPersistenceRegistration
{
    public static IServiceCollection AddPlatformSqlitePersistence(this IServiceCollection services, string metadataPath, string contentPath, string auditPath, long maximumAssetBytes = 100 * 1024 * 1024)
    {
        services.AddSingleton<IPlatformAssetContentProvider>(_ => new FileAssetContentProvider(contentPath));
        services.AddSingleton<IPlatformAssetMetadataRepository>(_ => new SqliteAssetMetadataRepository(metadataPath));
        services.AddSingleton<IPlatformAssetStore>(sp => new PersistentPlatformAssetStore(sp.GetRequiredService<IPlatformAssetContentProvider>(), sp.GetRequiredService<IPlatformAssetMetadataRepository>(), maximumAssetBytes));
        services.AddSingleton<SqlitePlatformAuditStore>(_ => new SqlitePlatformAuditStore(auditPath));
        services.AddSingleton<IPlatformAuditWriter>(sp => sp.GetRequiredService<SqlitePlatformAuditStore>());
        services.AddSingleton<IPlatformAuditReader>(sp => sp.GetRequiredService<SqlitePlatformAuditStore>());
        return services;
    }

    public static IServiceCollection AddPlatformIdentitySqlite(this IServiceCollection services, string path, IPlatformAuditWriter? auditWriter = null, IdentityOptions? options = null)
    {
        services.AddSingleton<SqliteIdentityStore>(_ => new SqliteIdentityStore(path));
        services.AddSingleton<IIdentityStore>(sp => sp.GetRequiredService<SqliteIdentityStore>());
        services.AddSingleton<IdentityService>(sp => new IdentityService(sp.GetRequiredService<IIdentityStore>(), auditWriter ?? sp.GetService<IPlatformAuditWriter>(), options));
        services.AddSingleton<IIdentityQueryV1>(sp => sp.GetRequiredService<IdentityService>());
        services.AddSingleton<IIdentityCommandV1>(sp => sp.GetRequiredService<IdentityService>());
        return services;
    }
}

public sealed class FileAssetContentProvider(string root) : IPlatformAssetContentProvider
{
    private readonly string _root = Prepare(root);
    private static string Prepare(string path) { Directory.CreateDirectory(path); return Path.GetFullPath(path); }
    private string Resolve(string handle) => Path.Combine(_root, Convert.ToHexString(Convert.FromHexString(handle)) + ".blob");
    public async ValueTask<string> StoreAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default)
    {
        var handle = Convert.ToHexString(Guid.NewGuid().ToByteArray()); var target = Resolve(handle); var temporary = target + ".partial"; long total = 0;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                { total += read; if (total > maximumBytes) throw new InvalidDataException("Asset exceeds the configured size limit."); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); }
            }
            File.Move(temporary, target); return handle;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
    public ValueTask<Stream?> OpenReadAsync(string handle, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); var path = Resolve(handle); return ValueTask.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true) : null); }
    public ValueTask DeleteAsync(string handle, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); var path = Resolve(handle); if (File.Exists(path)) File.Delete(path); return ValueTask.CompletedTask; }
}

public sealed class PersistentPlatformAssetStore : IPlatformAssetStore
{
    private readonly IPlatformAssetContentProvider _content;
    private readonly IPlatformAssetMetadataRepository _metadata;
    private readonly long _maximumBytes;
    public PersistentPlatformAssetStore(IPlatformAssetContentProvider contentProvider, IPlatformAssetMetadataRepository metadataRepository, long maximumBytes) { _content = contentProvider; _metadata = metadataRepository; _maximumBytes = maximumBytes; }
    public async ValueTask<AssetMetadata> StoreAsync(AssetType type, string mimeType, Stream content, AssetScope scope, AssetAccessPolicy accessPolicy, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mimeType) || mimeType.Length > 127 || string.IsNullOrWhiteSpace(scope.Domain) || string.IsNullOrWhiteSpace(scope.OwnerId)) throw new ArgumentException("Asset MIME type and ownership scope are required.");
        var handle = await _content.StoreAsync(content, _maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var input = await _content.OpenReadAsync(handle, cancellationToken).ConfigureAwait(false) ?? throw new IOException("Stored content could not be reopened.");
            var now = DateTimeOffset.UtcNow; var item = new AssetMetadata(AssetId.New(), type, mimeType, input.Length, Sanitize(metadata), scope, accessPolicy, handle, AssetStatus.Ready, now, now);
            await _metadata.SaveAsync(item, cancellationToken).ConfigureAwait(false); return item;
        }
        catch { await _content.DeleteAsync(handle, CancellationToken.None).ConfigureAwait(false); throw; }
    }
    public ValueTask<AssetMetadata?> GetMetadataAsync(AssetId id, CancellationToken cancellationToken = default) => _metadata.GetAsync(id, cancellationToken);
    public async ValueTask<Stream?> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default)
    { var item = await _metadata.GetAsync(id, cancellationToken).ConfigureAwait(false); return item is { Status: AssetStatus.Ready } ? await _content.OpenReadAsync(item.ContentHandle, cancellationToken).ConfigureAwait(false) : null; }
    public async ValueTask<bool> DeleteAsync(AssetId id, CancellationToken cancellationToken = default)
    { var item = await _metadata.GetAsync(id, cancellationToken).ConfigureAwait(false); if (item is null || !await _metadata.DeleteAsync(id, cancellationToken).ConfigureAwait(false)) return false; await _content.DeleteAsync(item.ContentHandle, cancellationToken).ConfigureAwait(false); return true; }
    private static readonly string[] SensitiveKeys = ["token", "secret", "password", "credential", "authorization", "content", "body", "key"];
    private static Dictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? values) => (values ?? new Dictionary<string, string>()).Where(p => !SensitiveKeys.Any(s => p.Key.Contains(s, StringComparison.OrdinalIgnoreCase))).Take(32).ToDictionary(p => p.Key[..Math.Min(p.Key.Length, 64)], p => p.Value[..Math.Min(p.Value.Length, 512)], StringComparer.OrdinalIgnoreCase);
}

public sealed class SqliteAssetMetadataRepository(string path) : IPlatformAssetMetadataRepository
{
    private readonly string _connection = Initialize(path, "CREATE TABLE IF NOT EXISTS platform_assets(id TEXT PRIMARY KEY,type INTEGER NOT NULL,mime TEXT NOT NULL,size INTEGER NOT NULL,metadata TEXT NOT NULL,scope_domain TEXT NOT NULL,owner_id TEXT NOT NULL,policy_id TEXT NOT NULL,public_read INTEGER NOT NULL,content_handle TEXT NOT NULL,status INTEGER NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL);");
    private static string Initialize(string path, string sql) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString(); using var c = new SqliteConnection(cs); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); return cs; }
    public async ValueTask SaveAsync(AssetMetadata metadata, CancellationToken cancellationToken = default) { await using var c = new SqliteConnection(_connection); await c.OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "INSERT INTO platform_assets VALUES($id,$type,$mime,$size,$metadata,$domain,$owner,$policy,$public,$handle,$status,$created,$updated)"; q.Parameters.AddWithValue("$id", metadata.Id.Value.ToString("D")); q.Parameters.AddWithValue("$type", (int)metadata.Type); q.Parameters.AddWithValue("$mime", metadata.MimeType); q.Parameters.AddWithValue("$size", metadata.Size); q.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata.Metadata)); q.Parameters.AddWithValue("$domain", metadata.Scope.Domain); q.Parameters.AddWithValue("$owner", metadata.Scope.OwnerId); q.Parameters.AddWithValue("$policy", metadata.AccessPolicy.PolicyId); q.Parameters.AddWithValue("$public", metadata.AccessPolicy.PublicRead); q.Parameters.AddWithValue("$handle", metadata.ContentHandle); q.Parameters.AddWithValue("$status", (int)metadata.Status); q.Parameters.AddWithValue("$created", metadata.CreatedAtUtc.ToString("O")); q.Parameters.AddWithValue("$updated", metadata.UpdatedAtUtc.ToString("O")); await q.ExecuteNonQueryAsync(cancellationToken); }
    public async ValueTask<AssetMetadata?> GetAsync(AssetId id, CancellationToken cancellationToken = default) { await using var c = new SqliteConnection(_connection); await c.OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT type,mime,size,metadata,scope_domain,owner_id,policy_id,public_read,content_handle,status,created_utc,updated_utc FROM platform_assets WHERE id=$id"; q.Parameters.AddWithValue("$id", id.Value.ToString("D")); await using var r = await q.ExecuteReaderAsync(cancellationToken); if (!await r.ReadAsync(cancellationToken)) return null; return new AssetMetadata(id,(AssetType)r.GetInt32(0),r.GetString(1),r.GetInt64(2),JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(3)) ?? new(),new(r.GetString(4),r.GetString(5)),new(r.GetString(6),r.GetBoolean(7)),r.GetString(8),(AssetStatus)r.GetInt32(9),DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture)); }
    public async ValueTask<bool> DeleteAsync(AssetId id, CancellationToken cancellationToken = default) { await using var c = new SqliteConnection(_connection); await c.OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText="DELETE FROM platform_assets WHERE id=$id"; q.Parameters.AddWithValue("$id",id.Value.ToString("D")); return await q.ExecuteNonQueryAsync(cancellationToken)>0; }
}

public sealed class SqlitePlatformAuditStore : IPlatformAuditWriter, IPlatformAuditReader
{
    private static readonly string[] SensitiveKeys = ["token", "secret", "password", "credential", "authorization", "content", "body", "key"];
    private readonly string _connection;
    public SqlitePlatformAuditStore(string path) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); _connection = new SqliteConnectionStringBuilder { DataSource=path, Mode=SqliteOpenMode.ReadWriteCreate }.ToString(); using var c=new SqliteConnection(_connection); c.Open(); using var q=c.CreateCommand(); q.CommandText="CREATE TABLE IF NOT EXISTS platform_audit(id INTEGER PRIMARY KEY AUTOINCREMENT,timestamp TEXT NOT NULL,actor TEXT,correlation TEXT,domain TEXT NOT NULL,action TEXT NOT NULL,resource_type TEXT NOT NULL,resource_id TEXT NOT NULL,result TEXT NOT NULL,metadata TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_platform_audit_timestamp ON platform_audit(timestamp DESC);"; q.ExecuteNonQuery(); }
    public async ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken=default) { var safe=record with { Metadata=record.Metadata.Where(p=>!SensitiveKeys.Any(s=>p.Key.Contains(s,StringComparison.OrdinalIgnoreCase))).Take(32).ToDictionary(p=>p.Key[..Math.Min(64,p.Key.Length)],p=>p.Value[..Math.Min(512,p.Value.Length)]) }; await using var c=new SqliteConnection(_connection); await c.OpenAsync(cancellationToken); await using var q=c.CreateCommand(); q.CommandText="INSERT INTO platform_audit(timestamp,actor,correlation,domain,action,resource_type,resource_id,result,metadata) VALUES($t,$actor,$correlation,$domain,$action,$type,$id,$result,$metadata)"; q.Parameters.AddWithValue("$t",safe.Timestamp.ToUniversalTime().ToString("O")); q.Parameters.AddWithValue("$actor",(object?)safe.ActorId??DBNull.Value); q.Parameters.AddWithValue("$correlation",(object?)safe.CorrelationId??DBNull.Value); q.Parameters.AddWithValue("$domain",safe.Domain); q.Parameters.AddWithValue("$action",safe.Action); q.Parameters.AddWithValue("$type",safe.ResourceType); q.Parameters.AddWithValue("$id",safe.ResourceId); q.Parameters.AddWithValue("$result",safe.Result); q.Parameters.AddWithValue("$metadata",JsonSerializer.Serialize(safe.Metadata)); await q.ExecuteNonQueryAsync(cancellationToken); }
    public async ValueTask<AuditPage> QueryAsync(AuditQuery query,CancellationToken cancellationToken=default) { var page=Math.Max(1,query.Page); var size=Math.Clamp(query.PageSize,1,200); await using var c=new SqliteConnection(_connection); await c.OpenAsync(cancellationToken); await using var q=c.CreateCommand(); q.CommandText="SELECT timestamp,actor,correlation,domain,action,resource_type,resource_id,result,metadata FROM platform_audit WHERE ($domain IS NULL OR domain=$domain) AND ($action IS NULL OR action=$action) AND ($type IS NULL OR resource_type=$type) AND ($id IS NULL OR resource_id=$id) AND ($actor IS NULL OR actor=$actor) AND ($correlation IS NULL OR correlation=$correlation) AND ($from IS NULL OR timestamp >= $from) AND ($to IS NULL OR timestamp <= $to) ORDER BY id DESC LIMIT $limit OFFSET $offset"; q.Parameters.AddWithValue("$domain",(object?)query.Domain??DBNull.Value); q.Parameters.AddWithValue("$action",(object?)query.Action??DBNull.Value); q.Parameters.AddWithValue("$type",(object?)query.ResourceType??DBNull.Value); q.Parameters.AddWithValue("$id",(object?)query.ResourceId??DBNull.Value); q.Parameters.AddWithValue("$actor",(object?)query.ActorId??DBNull.Value); q.Parameters.AddWithValue("$correlation",(object?)query.CorrelationId??DBNull.Value); q.Parameters.AddWithValue("$from",(object?)query.FromUtc?.ToUniversalTime().ToString("O")??DBNull.Value); q.Parameters.AddWithValue("$to",(object?)query.ToUtc?.ToUniversalTime().ToString("O")??DBNull.Value); q.Parameters.AddWithValue("$limit",size+1); q.Parameters.AddWithValue("$offset",(page-1)*size); var list=new List<AuditRecord>(); await using var r=await q.ExecuteReaderAsync(cancellationToken); while(await r.ReadAsync(cancellationToken)) list.Add(new(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture),r.IsDBNull(1)?null:r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(8))??new())); var more=list.Count>size; if(more) list.RemoveAt(list.Count-1); return new(list,page,size,more); }
}
