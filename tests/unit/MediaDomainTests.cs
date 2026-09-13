using System.Reflection;
using Hispania.Media.Domain;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Works;
using Xunit;

namespace Hispania.Media.Domain.Tests;

public sealed class MediaDomainTests
{
    [Theory]
    [InlineData("work-id.empty")]
    [InlineData("media-entity-id.empty")]
    [InlineData("asset-id.empty")]
    public void IdsRejectAnEmptyValue(string errorCode)
    {
        var exception = errorCode switch
        {
            "work-id.empty" => Assert.Throws<DomainValidationException>(() => new WorkId(Guid.Empty)),
            "media-entity-id.empty" => Assert.Throws<DomainValidationException>(() => new MediaEntityId(Guid.Empty)),
            "asset-id.empty" => Assert.Throws<DomainValidationException>(() => new AssetId(Guid.Empty)),
            _ => throw new InvalidOperationException("The test case is not supported.")
        };

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void IdsCompareByValueAndPreserveExistingValues()
    {
        var value = Guid.NewGuid();

        Assert.Equal(new WorkId(value), new WorkId(value));
        Assert.Equal(new MediaEntityId(value), new MediaEntityId(value));
        Assert.Equal(new AssetId(value), new AssetId(value));
        Assert.Equal(value, new WorkId(value).Value);
    }

    [Fact]
    public void IdTypesAreNotInterchangeableOrImplicitlyConvertible()
    {
        Assert.NotEqual(typeof(WorkId), typeof(MediaEntityId));
        Assert.NotEqual(typeof(WorkId), typeof(AssetId));
        Assert.NotEqual(typeof(MediaEntityId), typeof(AssetId));

        foreach (var idType in new[] { typeof(WorkId), typeof(MediaEntityId), typeof(AssetId) })
        {
            Assert.DoesNotContain(
                idType.GetMethods(BindingFlags.Public | BindingFlags.Static),
                method => method.Name == "op_Implicit");
        }
    }

    [Fact]
    public void NewIdsAreUuidVersion7()
    {
        Assert.Equal(7, WorkId.New().Value.Version);
        Assert.Equal(7, MediaEntityId.New().Value.Version);
        Assert.Equal(7, AssetId.New().Value.Version);
    }

    [Fact]
    public void WorkIsValidWithoutMediaEntities()
    {
        var work = new Work(WorkId.New(), WorkCategory.Movie);

        Assert.Equal(WorkCategory.Movie, work.Category);
        Assert.Empty(work.MediaEntities);
    }

    [Fact]
    public void WorkCreatesMediaEntitiesThatReferenceExactlyThatWork()
    {
        var work = new Work(WorkId.New(), WorkCategory.Movie);
        var mediaEntity = work.AddMediaEntity(MediaEntityId.New(), MediaEntityCategory.MovieRealisation);

        Assert.Equal(work.Id, mediaEntity.WorkId);
        Assert.Single(work.MediaEntities, mediaEntity);
    }

    [Fact]
    public void WorkRejectsDuplicateMediaEntityIds()
    {
        var work = new Work(WorkId.New(), WorkCategory.Movie);
        var mediaEntityId = MediaEntityId.New();
        work.AddMediaEntity(mediaEntityId, MediaEntityCategory.MovieRealisation);

        var exception = Assert.Throws<DomainValidationException>(
            () => work.AddMediaEntity(mediaEntityId, MediaEntityCategory.MovieRealisation));

        Assert.Equal("work.media-entity.duplicate", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Movie")]
    [InlineData("movie category")]
    public void WorkCategoryRejectsInvalidCodes(string code)
    {
        Assert.Throws<DomainValidationException>(() => WorkCategory.From(code));
    }

    [Fact]
    public void WorkCategoryAcceptsAValidFutureCode()
    {
        Assert.Equal("documentary-feature", WorkCategory.From("documentary-feature").Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("AudioRecording")]
    [InlineData("audio recording")]
    public void MediaEntityCategoryRejectsInvalidCodes(string code)
    {
        Assert.Throws<DomainValidationException>(() => MediaEntityCategory.From(code));
    }

    [Fact]
    public void MediaEntityCategoryAcceptsAValidFutureCode()
    {
        Assert.Equal("broadcast-realisation", MediaEntityCategory.From("broadcast-realisation").Code);
    }

    [Fact]
    public void AssetRequiresADefinedMediaKind()
    {
        Assert.Throws<DomainValidationException>(() => new Asset(AssetId.New(), (AssetMediaKind)99));
    }

    [Fact]
    public void AssetCanExistWithoutRoles()
    {
        var asset = new Asset(AssetId.New(), AssetMediaKind.Audio);

        Assert.Empty(asset.Roles);
    }

    [Fact]
    public void AssetAcceptsMultipleCompatibleRoles()
    {
        var asset = new Asset(
            AssetId.New(),
            AssetMediaKind.Audio,
            [AssetRole.Dub, AssetRole.Commentary]);

        Assert.Equal(2, asset.Roles.Count);
    }

    [Fact]
    public void AssetRejectsDuplicateRoles()
    {
        var asset = new Asset(AssetId.New(), AssetMediaKind.Audio, [AssetRole.Dub]);

        var exception = Assert.Throws<DomainValidationException>(() => asset.AddRole(AssetRole.Dub));

        Assert.Equal("asset.role.duplicate", exception.Code);
    }

    [Theory]
    [InlineData(AssetMediaKind.Audio, "subtitle")]
    [InlineData(AssetMediaKind.Video, "dub")]
    [InlineData(AssetMediaKind.Image, "main")]
    public void AssetRejectsIncompatibleRoles(AssetMediaKind mediaKind, string roleCode)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => new Asset(AssetId.New(), mediaKind, [AssetRole.From(roleCode)]));

        Assert.Equal("asset.role.incompatible", exception.Code);
    }

    [Fact]
    public void TextAcceptsSubtitleAndLyrics()
    {
        var asset = new Asset(AssetId.New(), AssetMediaKind.Text, [AssetRole.Subtitle, AssetRole.Lyrics]);

        Assert.Equal(2, asset.Roles.Count);
    }

    [Fact]
    public void AssetDoesNotModelTechnicalOrMetadataProperties()
    {
        var propertyNames = typeof(Asset).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("Codec", propertyNames);
        Assert.DoesNotContain("Language", propertyNames);
        Assert.DoesNotContain("Resolution", propertyNames);
    }
}
