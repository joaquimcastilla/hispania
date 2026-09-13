using Hispania.Media.Domain.Identity;

namespace Hispania.Media.Domain.Enrichment;

public sealed class MetadataAssertion
{
    public MetadataAssertion(
        MetadataAssertionId id,
        EnrichmentTarget target,
        MetadataField field,
        MetadataValue value,
        MetadataProvenance provenance,
        MetadataAssertionStatus status,
        MetadataAssertionId? supersededByAssertionId = null)
    {
        Id = id ?? throw new DomainValidationException("metadata-assertion.id.required", "A MetadataAssertion ID is required.");
        Target = target ?? throw new DomainValidationException("metadata-assertion.target.required", "A MetadataAssertion target is required.");
        Field = field ?? throw new DomainValidationException("metadata-assertion.field.required", "A MetadataAssertion field is required.");
        Value = value ?? throw new DomainValidationException("metadata-assertion.value.required", "A MetadataAssertion value is required.");
        Provenance = provenance ?? throw new DomainValidationException("metadata-assertion.provenance.required", "A MetadataAssertion provenance is required.");

        if (!Enum.IsDefined(status))
        {
            throw new DomainValidationException("metadata-assertion.status.invalid", "A MetadataAssertion must use a defined status.");
        }

        if (status == MetadataAssertionStatus.Superseded && supersededByAssertionId is null)
        {
            throw new DomainValidationException("metadata-assertion.supersession.required", "A superseded MetadataAssertion requires its successor ID.");
        }

        if (status != MetadataAssertionStatus.Superseded && supersededByAssertionId is not null)
        {
            throw new DomainValidationException("metadata-assertion.supersession.unexpected", "Only a superseded MetadataAssertion can declare its successor ID.");
        }

        if (supersededByAssertionId == id)
        {
            throw new DomainValidationException("metadata-assertion.supersession.self", "A MetadataAssertion cannot supersede itself.");
        }

        Status = status;
        SupersededByAssertionId = supersededByAssertionId;
    }

    public MetadataAssertionId Id { get; }

    public EnrichmentTarget Target { get; }

    public MetadataField Field { get; }

    public MetadataValue Value { get; }

    public MetadataProvenance Provenance { get; }

    public MetadataAssertionStatus Status { get; }

    public MetadataAssertionId? SupersededByAssertionId { get; }
}
