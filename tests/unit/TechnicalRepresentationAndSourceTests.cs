using Hispania.Media.Domain;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.TechnicalRepresentations;
using Xunit;

namespace Hispania.Media.Domain.Tests;

public sealed class TechnicalRepresentationAndSourceTests
{
    [Theory]
    [InlineData("technical-representation-id.empty")]
    [InlineData("media-source-id.empty")]
    public void NewIdentityTypesRejectEmptyValues(string errorCode)
    {
        var exception = errorCode switch
        {
            "technical-representation-id.empty" => Assert.Throws<DomainValidationException>(() => new TechnicalRepresentationId(Guid.Empty)),
            "media-source-id.empty" => Assert.Throws<DomainValidationException>(() => new MediaSourceId(Guid.Empty)),
            _ => throw new InvalidOperationException("The test case is not supported.")
        };

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void NewIdentityTypesGenerateVersion7AndRehydrateExactValues()
    {
        var value = Guid.NewGuid();

        Assert.Equal(7, TechnicalRepresentationId.New().Value.Version);
        Assert.Equal(7, MediaSourceId.New().Value.Version);
        Assert.Equal(value, new TechnicalRepresentationId(value).Value);
        Assert.Equal(value, new MediaSourceId(value).Value);
    }

    [Fact]
    public void VideoProfileIsValidWithTechnicalProperties()
    {
        var profile = CreateVideoProfile();

        Assert.Equal(AssetMediaKind.Video, profile.MediaKind);
        Assert.Equal(new PixelDimensions(1920, 1080), profile.Dimensions);
        Assert.Equal(new FrameRate(24000, 1001), profile.FrameRate);
    }

    [Theory]
    [InlineData(0, 1080, "pixel-dimensions.width.invalid")]
    [InlineData(1920, 0, "pixel-dimensions.height.invalid")]
    public void PixelDimensionsRejectInvalidValues(int width, int height, string errorCode)
    {
        var exception = Assert.Throws<DomainValidationException>(() => new PixelDimensions(width, height));

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void VideoProfileRejectsInvalidBitDepth()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => new VideoTechnicalProfile(new PixelDimensions(1920, 1080), Codec("av1"), new FrameRate(24, 1), 0));

        Assert.Equal("video-technical-profile.bit-depth.invalid", exception.Code);
    }

    [Fact]
    public void AudioProfileIsValidWithOptionalTechnicalValues()
    {
        var profile = new AudioTechnicalProfile(Codec("flac"), 48000, 2, 24, new BitRate(1_536_000));

        Assert.Equal(AssetMediaKind.Audio, profile.MediaKind);
        Assert.Equal(48000, profile.SampleRateHertz);
        Assert.Equal(2, profile.ChannelCount);
    }

    [Theory]
    [InlineData(0, 2, "audio-technical-profile.sample-rate.invalid")]
    [InlineData(48000, 0, "audio-technical-profile.channel-count.invalid")]
    public void AudioProfileRejectsInvalidSampleRateAndChannelCount(int sampleRate, int channelCount, string errorCode)
    {
        var exception = Assert.Throws<DomainValidationException>(() => new AudioTechnicalProfile(Codec("aac"), sampleRate, channelCount));

        Assert.Equal(errorCode, exception.Code);
    }

    [Fact]
    public void AudioProfileRejectsInvalidDeclaredBitDepth()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new AudioTechnicalProfile(Codec("aac"), 48000, 2, 0));

        Assert.Equal("audio-technical-profile.bit-depth.invalid", exception.Code);
    }

    [Fact]
    public void TextAndImageProfilesAreValid()
    {
        var text = new TextTechnicalProfile(Format("webvtt"), new CharacterEncoding("UTF-8"));
        var image = new ImageTechnicalProfile(new PixelDimensions(800, 600), Format("png"));

        Assert.Equal(AssetMediaKind.Text, text.MediaKind);
        Assert.Equal("utf-8", text.CharacterEncoding!.Code);
        Assert.Equal(AssetMediaKind.Image, image.MediaKind);
    }

    [Fact]
    public void DeclaredBitRateMustBePositiveBitsPerSecond()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new BitRate(0));

        Assert.Equal("bit-rate.invalid", exception.Code);
    }

    [Fact]
    public void FrameRateSupportsExactCommonRatesAndNormalizesEquality()
    {
        Assert.Equal(new FrameRate(24, 1), new FrameRate(48, 2));
        Assert.Equal(new FrameRate(24, 1), new FrameRate(24, 1));
        Assert.Equal(new FrameRate(24000, 1001), new FrameRate(24000, 1001));
        Assert.Equal(new FrameRate(30000, 1001), new FrameRate(30000, 1001));
    }

    [Theory]
    [InlineData(0, 1, "frame-rate.numerator.invalid")]
    [InlineData(1, 0, "frame-rate.denominator.invalid")]
    [InlineData(-24, 1, "frame-rate.numerator.invalid")]
    [InlineData(24, -1, "frame-rate.denominator.invalid")]
    public void FrameRateRejectsInvalidRationals(int numerator, int denominator, string errorCode)
    {
        var exception = Assert.Throws<DomainValidationException>(() => new FrameRate(numerator, denominator));

        Assert.Equal(errorCode, exception.Code);
    }

    [Theory]
    [InlineData(AssetMediaKind.Video)]
    [InlineData(AssetMediaKind.Audio)]
    [InlineData(AssetMediaKind.Text)]
    [InlineData(AssetMediaKind.Image)]
    public void AssetAcceptsAProfileWithTheSameMediaKind(AssetMediaKind mediaKind)
    {
        var asset = new Asset(AssetId.New(), mediaKind);
        var representation = asset.AddTechnicalRepresentation(TechnicalRepresentationId.New(), ProfileFor(mediaKind));

        Assert.Equal(asset.Id, representation.AssetId);
        Assert.Single(asset.TechnicalRepresentations, representation);
    }

    [Fact]
    public void AssetRejectsProfileWithDifferentMediaKind()
    {
        var asset = new Asset(AssetId.New(), AssetMediaKind.Audio);

        var exception = Assert.Throws<DomainValidationException>(
            () => asset.AddTechnicalRepresentation(TechnicalRepresentationId.New(), CreateVideoProfile()));

        Assert.Equal("technical-representation.profile.media-kind.mismatch", exception.Code);
    }

    [Fact]
    public void AssetAcceptsMultipleRepresentationsAndRejectsDuplicateIds()
    {
        var asset = new Asset(AssetId.New(), AssetMediaKind.Video);
        var id = TechnicalRepresentationId.New();
        asset.AddTechnicalRepresentation(id, CreateVideoProfile());
        asset.AddTechnicalRepresentation(TechnicalRepresentationId.New(), CreateVideoProfile());

        var exception = Assert.Throws<DomainValidationException>(() => asset.AddTechnicalRepresentation(id, CreateVideoProfile()));

        Assert.Equal(2, asset.TechnicalRepresentations.Count);
        Assert.Equal("asset.technical-representation.duplicate", exception.Code);
    }

    [Fact]
    public void MediaSourceUsesAValidOpaqueLocatorAndNormalizesItsScheme()
    {
        var source = new MediaSource(MediaSourceId.New(), new SourceLocator(new SourceScheme("FILE+Local"), "Media/Film.MKV"));

        Assert.Equal("file+local", source.Locator.Scheme.Value);
        Assert.Equal("Media/Film.MKV", source.Locator.OpaqueValue);
    }

    [Theory]
    [InlineData("1file")]
    [InlineData("file path")]
    [InlineData(" file")]
    public void SourceSchemeRejectsInvalidValues(string value)
    {
        Assert.Throws<DomainValidationException>(() => new SourceScheme(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" path")]
    [InlineData("path ")]
    [InlineData("line\nbreak")]
    public void SourceLocatorRejectsInvalidOpaqueValues(string opaqueValue)
    {
        Assert.Throws<DomainValidationException>(() => new SourceLocator(new SourceScheme("file"), opaqueValue));
    }

    [Fact]
    public void SourceLocatorRejectsAnOversizedOpaqueValue()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new SourceLocator(new SourceScheme("file"), new string('x', 4097)));

        Assert.Equal("source-locator.opaque-value.length", exception.Code);
    }

    [Fact]
    public void RepresentationCanExistWithoutSourceAndSourceCanExistWithoutRepresentation()
    {
        var representation = CreateVideoRepresentation();
        var source = CreateSource("unmapped.mkv");

        Assert.Empty(representation.SourceBindings);
        Assert.Equal("unmapped.mkv", source.Locator.OpaqueValue);
    }

    [Fact]
    public void RepresentationAcceptsDirectAndContainerBindings()
    {
        var representation = CreateVideoRepresentation();
        var directSource = CreateSource("direct.hevc");
        var containerSource = CreateSource("movie.mkv");

        var direct = representation.AddSourceBinding(directSource);
        var container = representation.AddSourceBinding(containerSource, Exposure(1));

        Assert.Null(direct.ContainerExposure);
        Assert.Equal(1, container.ContainerExposure!.StreamLocator.StreamIndex);
    }

    [Fact]
    public void StreamLocatorRejectsNegativeIndexes()
    {
        var exception = Assert.Throws<DomainValidationException>(() => new StreamLocator(-1));

        Assert.Equal("stream-locator.index.invalid", exception.Code);
    }

    [Fact]
    public void RepresentationCanUseSeveralSourcesAndSourceCanServeSeveralRepresentations()
    {
        var first = CreateVideoRepresentation();
        var second = CreateVideoRepresentation();
        var sharedSource = CreateSource("movie.mkv");
        var alternateSource = CreateSource("backup.mkv");

        first.AddSourceBinding(sharedSource, Exposure(0));
        first.AddSourceBinding(alternateSource, Exposure(0));
        second.AddSourceBinding(sharedSource, Exposure(1));

        Assert.Equal(2, first.SourceBindings.Count);
        Assert.Single(second.SourceBindings);
    }

    [Fact]
    public void RepresentationRejectsExactlyDuplicateBindingsButAllowsDifferentExposuresInOneSource()
    {
        var representation = CreateVideoRepresentation();
        var source = CreateSource("movie.mkv");
        representation.AddSourceBinding(source, Exposure(1));
        representation.AddSourceBinding(source, Exposure(4));

        var exception = Assert.Throws<DomainValidationException>(() => representation.AddSourceBinding(source, Exposure(1)));

        Assert.Equal(2, representation.SourceBindings.Count);
        Assert.Equal("technical-representation.source-binding.duplicate", exception.Code);
    }

    private static TechnicalRepresentation CreateVideoRepresentation()
    {
        var asset = new Asset(AssetId.New(), AssetMediaKind.Video);
        return asset.AddTechnicalRepresentation(TechnicalRepresentationId.New(), CreateVideoProfile());
    }

    private static MediaSource CreateSource(string opaqueValue) =>
        new(MediaSourceId.New(), new SourceLocator(new SourceScheme("file"), opaqueValue));

    private static ContainerExposure Exposure(int streamIndex) =>
        new(new ContainerFormat("hispania", "matroska"), new StreamLocator(streamIndex));

    private static TechnicalProfile ProfileFor(AssetMediaKind mediaKind) => mediaKind switch
    {
        AssetMediaKind.Video => CreateVideoProfile(),
        AssetMediaKind.Audio => new AudioTechnicalProfile(Codec("aac"), 48000, 2),
        AssetMediaKind.Text => new TextTechnicalProfile(Format("webvtt")),
        AssetMediaKind.Image => new ImageTechnicalProfile(new PixelDimensions(800, 600), Format("png")),
        _ => throw new InvalidOperationException("The test case is not supported.")
    };

    private static VideoTechnicalProfile CreateVideoProfile() =>
        new(new PixelDimensions(1920, 1080), Codec("av1"), new FrameRate(24000, 1001), 10, new BitRate(8_000_000));

    private static CodecIdentifier Codec(string code) => new("hispania", code);

    private static FormatIdentifier Format(string code) => new("hispania", code);
}
