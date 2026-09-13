using Hispania.Media.Domain;
using Hispania.Media.Domain.Enrichment;
using Hispania.Media.Domain.Identity;
using Xunit;

namespace Hispania.Media.Domain.Tests;

public sealed class EnrichmentTests
{
    [Theory]
    [InlineData("external-identifier-association-id.empty")]
    [InlineData("metadata-assertion-id.empty")]
    [InlineData("artwork-reference-id.empty")]
    public void EnrichmentIdentityTypesRejectEmptyValues(string errorCode)
    {
        var exception = errorCode switch
        {
            "external-identifier-association-id.empty" => Assert.Throws<DomainValidationException>(() => new ExternalIdentifierAssociationId(Guid.Empty)),
            "metadata-assertion-id.empty" => Assert.Throws<DomainValidationException>(() => new MetadataAssertionId(Guid.Empty)),
            "artwork-reference-id.empty" => Assert.Throws<DomainValidationException>(() => new ArtworkReferenceId(Guid.Empty)),
            _ => throw new InvalidOperationException("The test case is not supported.")
        };

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void EnrichmentIdentityTypesGenerateVersion7AndRehydrateExactValues()
    {
        var value = Guid.NewGuid();

        Assert.Equal(7, ExternalIdentifierAssociationId.New().Value.Version);
        Assert.Equal(7, MetadataAssertionId.New().Value.Version);
        Assert.Equal(7, ArtworkReferenceId.New().Value.Version);
        Assert.Equal(value, new ExternalIdentifierAssociationId(value).Value);
        Assert.Equal(value, new MetadataAssertionId(value).Value);
        Assert.Equal(value, new ArtworkReferenceId(value).Value);
    }

    [Fact]
    public void ExternalIdentifierNormalizesProviderAndNamespaceButPreservesCaseSensitiveValue()
    {
        var upper = new ExternalIdentifier(Provider("Example-Provider"), Namespace("Movie-Record"), "AbC-123");
        var lower = new ExternalIdentifier(Provider("example-provider"), Namespace("movie-record"), "abc-123");

        Assert.Equal("example-provider", upper.Provider.Value);
        Assert.Equal("movie-record", upper.Namespace.Value);
        Assert.Equal("AbC-123", upper.Value);
        Assert.NotEqual(upper, lower);
    }

    [Theory]
    [InlineData("1provider")]
    [InlineData("provider name")]
    [InlineData("provider_")]
    public void ExternalIdentifierProviderRejectsInvalidCodes(string value)
    {
        Assert.Throws<DomainValidationException>(() => Provider(value));
    }

    [Theory]
    [InlineData("1namespace")]
    [InlineData("namespace name")]
    [InlineData("namespace_")]
    public void ExternalIdentifierNamespaceRejectsInvalidCodes(string value)
    {
        Assert.Throws<DomainValidationException>(() => Namespace(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" value")]
    [InlineData("value ")]
    [InlineData("value\nnext")]
    public void ExternalIdentifierRejectsInvalidOpaqueValues(string value)
    {
        Assert.Throws<DomainValidationException>(() => Identifier(value));
    }

    [Fact]
    public void ExternalIdentifierNamespacesArePartOfEquality()
    {
        var provider = Provider("example");
        var first = new ExternalIdentifier(provider, Namespace("movie"), "42");
        var second = new ExternalIdentifier(provider, Namespace("collection"), "42");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EnrichmentTargetsSupportEveryAllowedTypeAndKeepTypeInEquality()
    {
        var value = Guid.NewGuid();
        var targets = new[]
        {
            EnrichmentTarget.For(new WorkId(value)),
            EnrichmentTarget.For(new MediaEntityId(value)),
            EnrichmentTarget.For(new ReleaseId(value)),
            EnrichmentTarget.For(new ReleaseGroupId(value)),
            EnrichmentTarget.For(new CollectionId(value)),
            EnrichmentTarget.For(new CreatorId(value)),
            EnrichmentTarget.For(new ArtworkReferenceId(value))
        };

        Assert.Equal(7, targets.Select(target => target.Kind).Distinct().Count());
        Assert.Equal(7, targets.Distinct().Count());
    }

    [Theory]
    [InlineData(ExternalIdentifierAssociationStatus.Candidate)]
    [InlineData(ExternalIdentifierAssociationStatus.Confirmed)]
    [InlineData(ExternalIdentifierAssociationStatus.Rejected)]
    public void AssociationPreservesLocalStateIdentifierAndTarget(ExternalIdentifierAssociationStatus status)
    {
        var identifier = Identifier("42");
        var target = EnrichmentTarget.For(WorkId.New());
        var association = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), identifier, target, status);

        Assert.Equal(status, association.Status);
        Assert.Equal(identifier, association.Identifier);
        Assert.Equal(target, association.Target);
    }

    [Fact]
    public void AssociationDoesNotUseGlobalStaticStateForDistinctCandidates()
    {
        var identifier = Identifier("42");
        var first = new ExternalIdentifierAssociation(
            ExternalIdentifierAssociationId.New(), identifier, EnrichmentTarget.For(WorkId.New()), ExternalIdentifierAssociationStatus.Candidate);
        var second = new ExternalIdentifierAssociation(
            ExternalIdentifierAssociationId.New(), identifier, EnrichmentTarget.For(WorkId.New()), ExternalIdentifierAssociationStatus.Candidate);

        Assert.NotEqual(first.Target, second.Target);
    }

    [Theory]
    [InlineData("title", "title")]
    [InlineData("Original-Title", "original-title")]
    [InlineData("future-field-42", "future-field-42")]
    public void MetadataFieldAcceptsNormalizedExtensibleCodes(string code, string expected)
    {
        Assert.Equal(expected, new MetadataField(code).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("field name")]
    [InlineData("field_")]
    public void MetadataFieldRejectsInvalidCodes(string code)
    {
        Assert.Throws<DomainValidationException>(() => new MetadataField(code));
    }

    [Fact]
    public void MetadataValuePreservesEachSupportedTypedVariant()
    {
        var date = new DateOnly(2024, 2, 3);
        var timestamp = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.FromHours(1));
        var values = new[]
        {
            MetadataValue.Text("Title"),
            MetadataValue.Integer(42),
            MetadataValue.Decimal(42m),
            MetadataValue.Boolean(true),
            MetadataValue.Date(date),
            MetadataValue.DateTimeOffset(timestamp)
        };

        Assert.Equal(6, values.Select(value => value.Kind).Distinct().Count());
        Assert.Equal("Title", values[0].TextValue);
        Assert.Equal(42L, values[1].IntegerValue);
        Assert.Equal(42m, values[2].DecimalValue);
        Assert.True(values[3].BooleanValue);
        Assert.Equal(date, values[4].DateValue);
        Assert.Equal(timestamp, values[5].DateTimeOffsetValue);
        Assert.NotEqual(values[1], values[2]);
    }

    [Fact]
    public void MetadataProvenanceSupportsManualLocalAndExternalOrigins()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var manual = new MetadataProvenance(MetadataProvenanceOrigin.Manual, observedAt, confidence: 0m);
        var local = new MetadataProvenance(MetadataProvenanceOrigin.LocalProcess, observedAt, confidence: 1m);
        var external = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, observedAt, Provider("example"), Identifier("42"), 0.5m);

        Assert.Equal(observedAt, manual.ObservedAt);
        Assert.Equal(MetadataProvenanceOrigin.LocalProcess, local.Origin);
        Assert.Equal("example", external.Provider!.Value);
        Assert.Equal(0.5m, external.Confidence);
    }

    [Fact]
    public void MetadataProvenanceRequiresExternalProviderAndValidConfidence()
    {
        var observedAt = DateTimeOffset.UtcNow;

        Assert.Equal(
            "metadata-provenance.provider.required",
            Assert.Throws<DomainValidationException>(() => new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, observedAt)).Code);
        Assert.Equal(
            "metadata-provenance.provider.unexpected",
            Assert.Throws<DomainValidationException>(() => new MetadataProvenance(MetadataProvenanceOrigin.Manual, observedAt, Provider("example"))).Code);
        Assert.Equal(
            "metadata-provenance.confidence.invalid",
            Assert.Throws<DomainValidationException>(() => new MetadataProvenance(MetadataProvenanceOrigin.Manual, observedAt, confidence: -0.1m)).Code);
        Assert.Equal(
            "metadata-provenance.confidence.invalid",
            Assert.Throws<DomainValidationException>(() => new MetadataProvenance(MetadataProvenanceOrigin.Manual, observedAt, confidence: 1.1m)).Code);
    }

    [Fact]
    public void MetadataAssertionsPreserveCompetingValuesAndValidateSupersessionShape()
    {
        var target = EnrichmentTarget.For(WorkId.New());
        var first = Assertion(MetadataAssertionId.New(), target, MetadataAssertionStatus.Active);
        var second = Assertion(MetadataAssertionId.New(), target, MetadataAssertionStatus.Active);
        var successorId = MetadataAssertionId.New();
        var superseded = Assertion(MetadataAssertionId.New(), target, MetadataAssertionStatus.Superseded, successorId);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(successorId, superseded.SupersededByAssertionId);
        Assert.Equal(
            "metadata-assertion.supersession.required",
            Assert.Throws<DomainValidationException>(() => Assertion(MetadataAssertionId.New(), target, MetadataAssertionStatus.Superseded)).Code);
        Assert.Equal(
            "metadata-assertion.supersession.unexpected",
            Assert.Throws<DomainValidationException>(() => Assertion(MetadataAssertionId.New(), target, MetadataAssertionStatus.Active, MetadataAssertionId.New())).Code);
        var self = MetadataAssertionId.New();
        Assert.Equal(
            "metadata-assertion.supersession.self",
            Assert.Throws<DomainValidationException>(() => Assertion(self, target, MetadataAssertionStatus.Superseded, self)).Code);
    }

    [Theory]
    [InlineData(MetadataAssertionStatus.Retracted)]
    [InlineData(MetadataAssertionStatus.Invalid)]
    public void MetadataAssertionsSupportTerminalNonSupersededStates(MetadataAssertionStatus status)
    {
        Assert.Equal(status, Assertion(MetadataAssertionId.New(), EnrichmentTarget.For(WorkId.New()), status).Status);
    }

    [Fact]
    public void MetadataAssertionsCanTargetArtworkReferences()
    {
        var target = EnrichmentTarget.For(ArtworkReferenceId.New());

        Assert.Equal(target, Assertion(MetadataAssertionId.New(), target, MetadataAssertionStatus.Active).Target);
    }

    [Fact]
    public void ArtworkReferencesAcceptCatalogTargetsAndRejectArtworkReferenceTargets()
    {
        foreach (var target in ArtworkTargets())
        {
            var artwork = new ArtworkReference(ArtworkReferenceId.New(), target, new ArtworkRole("poster"), Locator(), ManualProvenance());
            Assert.Equal(target, artwork.Target);
        }

        var exception = Assert.Throws<DomainValidationException>(
            () => new ArtworkReference(ArtworkReferenceId.New(), EnrichmentTarget.For(ArtworkReferenceId.New()), new ArtworkRole("poster"), Locator(), ManualProvenance()));

        Assert.Equal("artwork-reference.target.artwork-reference.invalid", exception.Code);
    }

    [Fact]
    public void ArtworkRoleAndLocatorAreExtensibleAndOpaque()
    {
        var role = new ArtworkRole("Future-Role");
        var locator = new ArtworkLocator("HTTPS", "provider/images/AbC-123");

        Assert.Equal("future-role", role.Code);
        Assert.Equal("https", locator.Scheme);
        Assert.Equal("provider/images/AbC-123", locator.OpaqueValue);
        Assert.Throws<DomainValidationException>(() => new ArtworkLocator("https", " locator"));
        Assert.Throws<DomainValidationException>(() => new ArtworkRole("provider_role"));
    }

    [Fact]
    public void ArtworkReferencesDoNotModelAssetsRepresentationsOrSources()
    {
        var propertyNames = typeof(ArtworkReference).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("Asset", propertyNames);
        Assert.DoesNotContain("TechnicalRepresentation", propertyNames);
        Assert.DoesNotContain("MediaSource", propertyNames);
    }

    private static ExternalIdentifierProvider Provider(string value) => new(value);

    private static ExternalIdentifierNamespace Namespace(string value) => new(value);

    private static ExternalIdentifier Identifier(string value) => new(Provider("example"), Namespace("work"), value);

    private static MetadataProvenance ManualProvenance() => new(MetadataProvenanceOrigin.Manual, DateTimeOffset.UtcNow);

    private static ArtworkLocator Locator() => new("https", "provider/images/42");

    private static MetadataAssertion Assertion(
        MetadataAssertionId id,
        EnrichmentTarget target,
        MetadataAssertionStatus status,
        MetadataAssertionId? supersededByAssertionId = null) =>
        new(id, target, new MetadataField("title"), MetadataValue.Text("A title"), ManualProvenance(), status, supersededByAssertionId);

    private static IEnumerable<EnrichmentTarget> ArtworkTargets()
    {
        yield return EnrichmentTarget.For(WorkId.New());
        yield return EnrichmentTarget.For(MediaEntityId.New());
        yield return EnrichmentTarget.For(ReleaseId.New());
        yield return EnrichmentTarget.For(ReleaseGroupId.New());
        yield return EnrichmentTarget.For(CollectionId.New());
        yield return EnrichmentTarget.For(CreatorId.New());
    }
}
