using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.Metrics;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Contracts.Storage;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

/// <summary>Reconciles only Platform-owned Assets and their configured Storage provider.</summary>
public sealed class SqliteAssetReconciliationOwnerV1 : IReconciliationOwnerV1
{
    private static readonly Meter Meter = new("Hispania.Reconciliation", "1.0");
    private static readonly Counter<long> Findings = Meter.CreateCounter<long>("hispania.reconciliation.findings");
    private readonly string connectionString;
    private readonly IStorageProviderResolverV1 providers;
    private readonly IStorageInventoryV1 inventory;
    private readonly TimeProvider clock;
    private readonly TimeSpan orphanGrace;

    public SqliteAssetReconciliationOwnerV1(string metadataPath, IStorageProviderResolverV1 providers,
        IStorageInventoryV1 inventory, TimeSpan? orphanGrace = null, TimeProvider? clock = null)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = metadataPath,
            Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        this.providers = providers;
        this.inventory = inventory;
        this.orphanGrace = orphanGrace ?? TimeSpan.FromHours(24);
        if (this.orphanGrace < TimeSpan.FromMinutes(1) || this.orphanGrace > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(orphanGrace));
        this.clock = clock ?? TimeProvider.System;
    }

    public MaintenanceOwnerIdV1 OwnerId => new("platform.assets.storage");

    /// <summary>Read-only inspection of unresolved logical quarantine. No bytes are moved or deleted.</summary>
    public async ValueTask<IReadOnlyList<ReconciliationQuarantineEntryV1>> ListQuarantineAsync(
        int maximumItems, CancellationToken cancellationToken = default)
    {
        if (maximumItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        await using var c = new SqliteConnection(connectionString);
        await c.OpenAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT finding_id,content_handle,reason,observed_utc FROM asset_reconciliation_quarantine WHERE resolved_utc IS NULL ORDER BY observed_utc,finding_id LIMIT $limit";
        q.Parameters.AddWithValue("$limit", maximumItems);
        var entries = new List<ReconciliationQuarantineEntryV1>();
        await using var reader = await q.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)));
        return entries;
    }

    public async ValueTask<ReconciliationScanV1> ScanAsync(int maximumItems, string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (continuationToken is not null && !ValidCursor(continuationToken))
            throw new ArgumentException("Reconciliation cursor is malformed.", nameof(continuationToken));
        var started = clock.GetUtcNow();
        var findings = new List<ReconciliationFindingV1>();
        var examined = 0L;
        await using var c = new SqliteConnection(connectionString);
        await c.OpenAsync(cancellationToken);
        if (continuationToken is null || continuationToken.StartsWith("metadata:", StringComparison.Ordinal))
        {
            var after = continuationToken is null ? string.Empty : continuationToken["metadata:".Length..];
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT id,content_handle,size,provider_id,status FROM platform_assets WHERE id>$after ORDER BY id LIMIT $limit";
            q.Parameters.AddWithValue("$after", after); q.Parameters.AddWithValue("$limit", maximumItems + 1);
            var rows = new List<(string Id, string Handle, long Size, string Provider, AssetStatus Status)>();
            await using (var r = await q.ExecuteReaderAsync(cancellationToken))
                while (await r.ReadAsync(cancellationToken))
                    rows.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), (AssetStatus)r.GetInt32(4)));
            foreach (var row in rows.Take(maximumItems))
            {
                cancellationToken.ThrowIfCancellationRequested(); examined++;
                if (row.Status == AssetStatus.Deleted) continue;
                StorageObjectMetadata? content;
                try { content = await providers.Resolve(row.Provider).GetMetadataAsync(new(row.Handle), cancellationToken); }
                catch (Exception ex) when (ex is IOException or ArgumentException or StorageProviderNotFoundException)
                {
                    findings.Add(Finding(ReconciliationFindingKindV1.Unknown, ReconciliationSeverityV1.Warning,
                        row.Id, started, "Storage metadata could not be validated.", false));
                    continue;
                }
                if (content is null)
                    findings.Add(Finding(ReconciliationFindingKindV1.MissingContent, ReconciliationSeverityV1.Critical,
                        row.Id, started, "Asset metadata references missing content.", false));
                else if (content.Size != row.Size)
                    findings.Add(Finding(ReconciliationFindingKindV1.Mismatch, ReconciliationSeverityV1.Warning,
                        row.Id, started, "Asset size differs from Storage size.", false));
            }
            var next = rows.Count > maximumItems ? "metadata:" + rows[maximumItems - 1].Id : "storage:";
            RecordFindings(findings);
            return new(OwnerId, started, clock.GetUtcNow(), false, examined, findings, next);
        }

        var storageCursor = continuationToken["storage:".Length..];
        var page = await inventory.ScanAsync(maximumItems, storageCursor.Length == 0 ? null : storageCursor,
            cancellationToken);
        foreach (var item in page.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested(); examined++;
            var handle = item.Handle.Value;
            var id = handle.StartsWith("storage://filesystem/", StringComparison.Ordinal)
                ? handle["storage://filesystem/".Length..] : handle;
            await using var check = c.CreateCommand();
            check.CommandText = "SELECT 1 FROM platform_assets WHERE provider_id=$provider AND (content_handle=$handle COLLATE NOCASE OR content_handle=$legacy COLLATE NOCASE) LIMIT 1";
            check.Parameters.AddWithValue("$provider", inventory.ProviderId);
            check.Parameters.AddWithValue("$handle", "storage://filesystem/" + id.ToLowerInvariant());
            check.Parameters.AddWithValue("$legacy", id.ToUpperInvariant());
            if (await check.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await MarkResolvedAsync(c, StableId(ReconciliationFindingKindV1.OrphanedContent, handle), started, cancellationToken);
                continue;
            }
            var findingId = StableId(ReconciliationFindingKindV1.OrphanedContent, handle);
            var cutoff = started > DateTimeOffset.MinValue + orphanGrace ? started - orphanGrace : DateTimeOffset.MinValue;
            var firstQuarantined = await FirstQuarantinedAsync(c, findingId, cancellationToken);
            var mature = item.CreatedAtUtc <= cutoff || firstQuarantined is not null;
            if (mature) await QuarantineAsync(c, findingId, handle, started, cancellationToken);
            findings.Add(new(findingId, OwnerId, ReconciliationFindingKindV1.OrphanedContent,
                mature ? ReconciliationSeverityV1.Warning : ReconciliationSeverityV1.Information,
                started, mature ? "Unreferenced owned content is logically quarantined." : "Unreferenced owned content is within the publication grace period.",
                mature, handle, firstQuarantined ?? started, started,
                "Owned Storage object has no Asset metadata reference.",
                mature ? "Inspect the owned object and either restore its Asset metadata or explicitly resolve quarantine."
                    : "Wait for the grace period and scan again.",
                mature ? ReconciliationAutoFixV1.Manual : ReconciliationAutoFixV1.Informational));
        }
        RecordFindings(findings);
        return new(OwnerId, started, clock.GetUtcNow(), page.Complete, examined, findings,
            page.Complete ? null : "storage:" + page.ContinuationToken);
    }

    private void RecordFindings(IEnumerable<ReconciliationFindingV1> findings)
    {
        foreach (var finding in findings)
            Findings.Add(1, new KeyValuePair<string, object?>("owner", OwnerId.Value),
                new KeyValuePair<string, object?>("kind", finding.Kind.ToString()));
    }

    private static bool ValidCursor(string value)
    {
        if (value == "storage:") return true;
        if (value.StartsWith("metadata:", StringComparison.Ordinal))
            return Guid.TryParseExact(value["metadata:".Length..], "D", out _);
        if (!value.StartsWith("storage:", StringComparison.Ordinal)) return false;
        var name = value["storage:".Length..];
        return name.Length == 37 && name.EndsWith(".blob", StringComparison.Ordinal) && name[..32].All(Uri.IsHexDigit);
    }

    private ReconciliationFindingV1 Finding(ReconciliationFindingKindV1 kind, ReconciliationSeverityV1 severity,
        string subject, DateTimeOffset observed, string reason, bool quarantined)
        => new(StableId(kind, subject), OwnerId, kind, severity, observed, reason, quarantined, subject,
            observed, observed, reason, "Inspect Asset metadata and Storage state; repair the owner record manually.",
            ReconciliationAutoFixV1.Manual);

    private string StableId(ReconciliationFindingKindV1 kind, string subject)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OwnerId.Value + "|" + kind + "|" + subject)));

    private static async ValueTask QuarantineAsync(SqliteConnection c, string findingId, string handle,
        DateTimeOffset observed, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO asset_reconciliation_quarantine(finding_id,content_handle,reason,observed_utc,resolved_utc) VALUES($id,$handle,'orphaned-content',$now,NULL) ON CONFLICT(finding_id) DO UPDATE SET resolved_utc=NULL";
        q.Parameters.AddWithValue("$id", findingId); q.Parameters.AddWithValue("$handle", handle);
        q.Parameters.AddWithValue("$now", observed.ToUniversalTime().ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask<DateTimeOffset?> FirstQuarantinedAsync(SqliteConnection c, string findingId, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT observed_utc FROM asset_reconciliation_quarantine WHERE finding_id=$id AND resolved_utc IS NULL";
        q.Parameters.AddWithValue("$id", findingId);
        var value = await q.ExecuteScalarAsync(ct);
        return value is string stamp ? DateTimeOffset.Parse(stamp, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static async ValueTask MarkResolvedAsync(SqliteConnection c, string findingId,
        DateTimeOffset observed, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "UPDATE asset_reconciliation_quarantine SET resolved_utc=$now WHERE finding_id=$id AND resolved_utc IS NULL";
        q.Parameters.AddWithValue("$id", findingId); q.Parameters.AddWithValue("$now", observed.ToUniversalTime().ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
    }
}
