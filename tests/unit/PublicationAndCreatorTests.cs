using Hispania.Media.Domain;
using Hispania.Media.Domain.Creators;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Publication;
using Xunit;

namespace Hispania.Media.Domain.Tests;

public sealed class PublicationAndCreatorTests
{
    [Theory]
    [InlineData("release-id.empty")]
    [InlineData("release-group-id.empty")]
    [InlineData("collection-id.empty")]
    [InlineData("collection-membership-id.empty")]
    [InlineData("creator-id.empty")]
    [InlineData("creator-contribution-id.empty")]
    public void NewIdentityTypesRejectEmptyValues(string errorCode)
    {
        var exception = errorCode switch
        {
            "release-id.empty" => Assert.Throws<DomainValidationException>(() => new ReleaseId(Guid.Empty)),
            "release-group-id.empty" => Assert.Throws<DomainValidationException>(() => new ReleaseGroupId(Guid.Empty)),
            "collection-id.empty" => Assert.Throws<DomainValidationException>(() => new CollectionId(Guid.Empty)),
            "collection-membership-id.empty" => Assert.Throws<DomainValidationException>(() => new CollectionMembershipId(Guid.Empty)),
            "creator-id.empty" => Assert.Throws<DomainValidationException>(() => new CreatorId(Guid.Empty)),
            "creator-contribution-id.empty" => Assert.Throws<DomainValidationException>(() => new CreatorContributionId(Guid.Empty)),
            _ => throw new InvalidOperationException("The test case is not supported.")
        };

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void NewIdentityTypesGenerateVersion7AndPreserveExistingValues()
    {
        var value = Guid.NewGuid();

        Assert.Equal(7, ReleaseId.New().Value.Version);
        Assert.Equal(7, ReleaseGroupId.New().Value.Version);
        Assert.Equal(7, CollectionId.New().Value.Version);
        Assert.Equal(7, CollectionMembershipId.New().Value.Version);
        Assert.Equal(7, CreatorId.New().Value.Version);
        Assert.Equal(7, CreatorContributionId.New().Value.Version);
        Assert.Equal(value, new ReleaseId(value).Value);
        Assert.Equal(value, new ReleaseGroupId(value).Value);
        Assert.Equal(value, new CollectionId(value).Value);
        Assert.Equal(value, new CollectionMembershipId(value).Value);
        Assert.Equal(value, new CreatorId(value).Value);
        Assert.Equal(value, new CreatorContributionId(value).Value);
    }

    [Fact]
    public void ReleaseIsValidWhenEmptyAndCanContainDifferentMediaEntities()
    {
        var release = new Release(ReleaseId.New());
        var first = release.AddMediaEntity(MediaEntityId.New());
        var second = release.AddMediaEntity(MediaEntityId.New());

        Assert.Empty(new Release(ReleaseId.New()).Memberships);
        Assert.Equal(2, release.Memberships.Count);
        Assert.NotEqual(first.MediaEntityId, second.MediaEntityId);
    }

    [Fact]
    public void ReleaseRejectsDuplicateMediaEntityButDifferentReleasesMayContainIt()
    {
        var mediaEntityId = MediaEntityId.New();
        var firstRelease = new Release(ReleaseId.New());
        var secondRelease = new Release(ReleaseId.New());
        firstRelease.AddMediaEntity(mediaEntityId);
        secondRelease.AddMediaEntity(mediaEntityId);

        var exception = Assert.Throws<DomainValidationException>(() => firstRelease.AddMediaEntity(mediaEntityId));

        Assert.Single(secondRelease.Memberships);
        Assert.Equal("release.membership.duplicate", exception.Code);
    }

    [Fact]
    public void ReleaseGroupIsValidWhenEmptyAndCanContainDifferentReleases()
    {
        var group = new ReleaseGroup(ReleaseGroupId.New());
        var first = group.AddRelease(ReleaseId.New());
        var second = group.AddRelease(ReleaseId.New());

        Assert.Empty(new ReleaseGroup(ReleaseGroupId.New()).Memberships);
        Assert.Equal(2, group.Memberships.Count);
        Assert.NotEqual(first.ReleaseId, second.ReleaseId);
    }

    [Fact]
    public void ReleaseGroupRejectsDuplicatesButReleaseMayAppearInDifferentGroups()
    {
        var releaseId = ReleaseId.New();
        var firstGroup = new ReleaseGroup(ReleaseGroupId.New());
        var secondGroup = new ReleaseGroup(ReleaseGroupId.New());
        firstGroup.AddRelease(releaseId);
        secondGroup.AddRelease(releaseId);

        var exception = Assert.Throws<DomainValidationException>(() => firstGroup.AddRelease(releaseId));

        Assert.Single(secondGroup.Memberships);
        Assert.Equal("release-group.membership.duplicate", exception.Code);
    }

    [Fact]
    public void CollectionIsValidWhenEmptyAndOrdersMembershipsByPosition()
    {
        var collection = new Collection(CollectionId.New());
        var first = collection.AddMembership(CollectionMembershipId.New(), MediaEntityId.New(), new CollectionPosition(10));
        var second = collection.AddMembership(CollectionMembershipId.New(), MediaEntityId.New(), new CollectionPosition(1));

        Assert.Empty(new Collection(CollectionId.New()).Memberships);
        Assert.Equal([second.Id, first.Id], collection.Memberships.Select(membership => membership.Id));
        Assert.All(collection.Memberships, membership => Assert.Equal(collection.Id, membership.CollectionId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CollectionPositionRejectsNonPositiveValues(int value)
    {
        var exception = Assert.Throws<DomainValidationException>(() => new CollectionPosition(value));

        Assert.Equal("collection-position.non-positive", exception.Code);
    }

    [Fact]
    public void CollectionRejectsDuplicatePositionsButAllowsRepeatedMediaEntityAtDifferentPositions()
    {
        var collection = new Collection(CollectionId.New());
        var mediaEntityId = MediaEntityId.New();
        collection.AddMembership(CollectionMembershipId.New(), mediaEntityId, new CollectionPosition(1));
        collection.AddMembership(CollectionMembershipId.New(), mediaEntityId, new CollectionPosition(5));

        var exception = Assert.Throws<DomainValidationException>(
            () => collection.AddMembership(CollectionMembershipId.New(), MediaEntityId.New(), new CollectionPosition(5)));

        Assert.Equal(2, collection.Memberships.Count);
        Assert.Equal("collection.membership.position.duplicate", exception.Code);
    }

    [Fact]
    public void MediaEntityMayBelongToDifferentCollections()
    {
        var mediaEntityId = MediaEntityId.New();
        var first = new Collection(CollectionId.New());
        var second = new Collection(CollectionId.New());

        first.AddMembership(CollectionMembershipId.New(), mediaEntityId, new CollectionPosition(1));
        second.AddMembership(CollectionMembershipId.New(), mediaEntityId, new CollectionPosition(1));

        Assert.Single(first.Memberships);
        Assert.Single(second.Memberships);
    }

    [Theory]
    [InlineData(CreatorKind.Person)]
    [InlineData(CreatorKind.Organization)]
    public void CreatorAcceptsDefinedKinds(CreatorKind kind)
    {
        var creator = new Creator(CreatorId.New(), kind);

        Assert.Equal(kind, creator.Kind);
        Assert.Empty(creator.Contributions);
    }

    [Theory]
    [InlineData("performer", "performer")]
    [InlineData("actor", "actor")]
    [InlineData("director", "director")]
    [InlineData("composer", "composer")]
    [InlineData("producer", "producer")]
    [InlineData("future-role-42", "future-role-42")]
    [InlineData("Director", "director")]
    public void ContributionRoleAcceptsKnownAndFutureCodes(string code, string expectedCode)
    {
        Assert.Equal(expectedCode, new ContributionRole(code).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("actor role")]
    [InlineData("-actor")]
    [InlineData("actor_")]
    public void ContributionRoleRejectsInvalidCodes(string code)
    {
        var exception = Assert.Throws<DomainValidationException>(() => new ContributionRole(code));

        Assert.True(
            exception.Code is "contribution-role.code.required" or "contribution-role.code.invalid",
            "An invalid contribution role must return a role-code validation error.");
    }

    [Fact]
    public void ContributionTargetsPreserveTheirTypedVariantAndIdentity()
    {
        var sharedValue = Guid.NewGuid();
        var work = ContributionTarget.For(new WorkId(sharedValue));
        var mediaEntity = ContributionTarget.For(new MediaEntityId(sharedValue));
        var release = ContributionTarget.For(new ReleaseId(sharedValue));

        Assert.Equal(ContributionTargetKind.Work, work.Kind);
        Assert.Equal(new WorkId(sharedValue), work.WorkId);
        Assert.Equal(ContributionTargetKind.MediaEntity, mediaEntity.Kind);
        Assert.Equal(new MediaEntityId(sharedValue), mediaEntity.MediaEntityId);
        Assert.Equal(ContributionTargetKind.Release, release.Kind);
        Assert.Equal(new ReleaseId(sharedValue), release.ReleaseId);
        Assert.NotEqual(work, mediaEntity);
        Assert.NotEqual(mediaEntity, release);
        Assert.NotEqual(release, work);
    }

    [Fact]
    public void ContributionTargetFactoriesRejectMissingTypedIds()
    {
        Assert.Equal(
            "contribution-target.work-id.required",
            Assert.Throws<DomainValidationException>(() => ContributionTarget.For((WorkId)null!)).Code);
        Assert.Equal(
            "contribution-target.media-entity-id.required",
            Assert.Throws<DomainValidationException>(() => ContributionTarget.For((MediaEntityId)null!)).Code);
        Assert.Equal(
            "contribution-target.release-id.required",
            Assert.Throws<DomainValidationException>(() => ContributionTarget.For((ReleaseId)null!)).Code);
    }

    [Fact]
    public void CreatorContributionsSupportEveryTargetAndRejectOnlyExactDuplicates()
    {
        var creator = new Creator(CreatorId.New(), CreatorKind.Person);
        var composer = new ContributionRole("composer");
        var performer = new ContributionRole("performer");
        var workTarget = ContributionTarget.For(WorkId.New());
        var mediaEntityTarget = ContributionTarget.For(MediaEntityId.New());
        var releaseTarget = ContributionTarget.For(ReleaseId.New());

        creator.AddContribution(CreatorContributionId.New(), workTarget, composer);
        creator.AddContribution(CreatorContributionId.New(), workTarget, performer);
        creator.AddContribution(CreatorContributionId.New(), mediaEntityTarget, composer);
        creator.AddContribution(CreatorContributionId.New(), releaseTarget, composer);

        var exception = Assert.Throws<DomainValidationException>(
            () => creator.AddContribution(CreatorContributionId.New(), workTarget, composer));

        Assert.Equal(4, creator.Contributions.Count);
        Assert.All(creator.Contributions, contribution => Assert.Equal(creator.Id, contribution.CreatorId));
        Assert.Equal("creator.contribution.duplicate", exception.Code);
    }
}
