using Hispania.Media.Domain.Identity;
using System.Collections.ObjectModel;

namespace Hispania.Media.Domain.Enrichment;

public sealed class ExternalIdentifierAssociation
{
    private readonly List<ExternalIdentifierAssociationLifecycleEntry> lifecycleEntries;
    private readonly List<ExternalIdentifierAssociationRelation> relatedAssociations;

    public ExternalIdentifierAssociation(
        ExternalIdentifierAssociationId id,
        ExternalIdentifier identifier,
        EnrichmentTarget target,
        ExternalIdentifierAssociationLifecycleEntry initialLifecycleEntry)
    {
        Id = id ?? throw new DomainValidationException("external-identifier-association.id.required", "An ExternalIdentifierAssociation ID is required.");
        Identifier = identifier ?? throw new DomainValidationException("external-identifier-association.identifier.required", "An ExternalIdentifierAssociation identifier is required.");
        Target = target ?? throw new DomainValidationException("external-identifier-association.target.required", "An ExternalIdentifierAssociation target is required.");
        ArgumentNullException.ThrowIfNull(initialLifecycleEntry);

        lifecycleEntries = [initialLifecycleEntry];
        relatedAssociations = [];
        LifecycleEntries = new ReadOnlyCollection<ExternalIdentifierAssociationLifecycleEntry>(lifecycleEntries);
        RelatedAssociations = new ReadOnlyCollection<ExternalIdentifierAssociationRelation>(relatedAssociations);
    }

    public ExternalIdentifierAssociationId Id { get; }

    public ExternalIdentifier Identifier { get; }

    public EnrichmentTarget Target { get; }

    public ExternalIdentifierAssociationStatus CurrentStatus => lifecycleEntries[^1].Status;

    public IReadOnlyList<ExternalIdentifierAssociationLifecycleEntry> LifecycleEntries { get; }

    public IReadOnlyList<ExternalIdentifierAssociationRelation> RelatedAssociations { get; }

    public void TransitionTo(ExternalIdentifierAssociationLifecycleEntry lifecycleEntry)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEntry);

        if (!IsAllowedTransition(CurrentStatus, lifecycleEntry.Status))
        {
            throw new DomainValidationException("external-identifier-association.transition.invalid", "The requested ExternalIdentifierAssociation lifecycle transition is not allowed.");
        }

        lifecycleEntries.Add(lifecycleEntry);
    }

    public void AddRelatedAssociation(ExternalIdentifierAssociationRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        if (relation.RelatedAssociationId == Id)
        {
            throw new DomainValidationException("external-identifier-association.relation.self", "An ExternalIdentifierAssociation cannot relate to itself.");
        }

        if (relatedAssociations.Any(existing =>
                existing.RelatedAssociationId == relation.RelatedAssociationId &&
                existing.Kind == relation.Kind))
        {
            throw new DomainValidationException("external-identifier-association.relation.duplicate", "An ExternalIdentifierAssociation cannot contain duplicate relation kinds for the same related association.");
        }

        relatedAssociations.Add(relation);
    }

    private static bool IsAllowedTransition(
        ExternalIdentifierAssociationStatus currentStatus,
        ExternalIdentifierAssociationStatus nextStatus) =>
        (currentStatus, nextStatus) switch
        {
            (ExternalIdentifierAssociationStatus.Candidate, ExternalIdentifierAssociationStatus.Confirmed) => true,
            (ExternalIdentifierAssociationStatus.Candidate, ExternalIdentifierAssociationStatus.Rejected) => true,
            (ExternalIdentifierAssociationStatus.Confirmed, ExternalIdentifierAssociationStatus.Invalidated) => true,
            _ => false
        };
}
