namespace Hispania.Platform.Contracts.Audit;

public sealed record AuditRecord(
    DateTimeOffset Timestamp,
    string? ActorId,
    string? CorrelationId,
    string Domain,
    string Action,
    string ResourceType,
    string ResourceId,
    string Result,
    IReadOnlyDictionary<string, string> Metadata);

public interface IPlatformAuditWriter
{
    ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

public sealed record AuditQuery(string? Domain = null, string? Action = null, string? ResourceType = null,
    string? ResourceId = null, string? ActorId = null, string? CorrelationId = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, int Page = 1, int PageSize = 50);
public sealed record AuditPage(IReadOnlyList<AuditRecord> Items, int Page, int PageSize, bool HasMore);
public interface IPlatformAuditReader
{
    ValueTask<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default);
}
