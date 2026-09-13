namespace Hispania.Media.Domain.Enrichment;

public sealed record ExternalIdentifierAssociationLifecycleEntry
{
    public ExternalIdentifierAssociationLifecycleEntry(
        ExternalIdentifierAssociationStatus status,
        DateTimeOffset occurredAt,
        MetadataProvenance evidence,
        string? reason = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new DomainValidationException("external-identifier-association.lifecycle-entry.status.invalid", "An ExternalIdentifierAssociation lifecycle entry must use a defined status.");
        }

        Evidence = evidence ?? throw new DomainValidationException("external-identifier-association.lifecycle-entry.evidence.required", "An ExternalIdentifierAssociation lifecycle entry requires evidence.");
        Reason = ValidateReason(reason, "external-identifier-association.lifecycle-entry.reason");

        if (status is ExternalIdentifierAssociationStatus.Rejected or ExternalIdentifierAssociationStatus.Invalidated && Reason is null)
        {
            throw new DomainValidationException("external-identifier-association.lifecycle-entry.reason.required", "Rejected and invalidated ExternalIdentifierAssociation lifecycle entries require a reason.");
        }

        Status = status;
        OccurredAt = occurredAt;
    }

    public ExternalIdentifierAssociationStatus Status { get; }

    public DateTimeOffset OccurredAt { get; }

    public MetadataProvenance Evidence { get; }

    public string? Reason { get; }

    internal static string? ValidateReason(string? reason, string codePrefix)
    {
        if (reason is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason != reason.Trim() || reason.Any(char.IsControl))
        {
            throw new DomainValidationException(codePrefix + ".invalid", "An ExternalIdentifierAssociation reason must be non-empty and cannot contain exterior whitespace or control characters.");
        }

        return reason;
    }
}
