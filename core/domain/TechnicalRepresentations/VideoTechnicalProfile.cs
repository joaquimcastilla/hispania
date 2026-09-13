using Hispania.Media.Domain.Assets;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record VideoTechnicalProfile : TechnicalProfile
{
    public VideoTechnicalProfile(PixelDimensions dimensions, CodecIdentifier codec, FrameRate frameRate, int bitDepth, BitRate? bitRate = null)
    {
        Dimensions = dimensions ?? throw new DomainValidationException("video-technical-profile.dimensions.required", "Video dimensions are required.");
        Codec = codec ?? throw new DomainValidationException("video-technical-profile.codec.required", "A video codec is required.");
        FrameRate = frameRate ?? throw new DomainValidationException("video-technical-profile.frame-rate.required", "A video frame rate is required.");

        if (bitDepth <= 0)
        {
            throw new DomainValidationException("video-technical-profile.bit-depth.invalid", "Video bit depth must be positive.");
        }

        BitDepth = bitDepth;
        BitRate = bitRate;
    }

    public override AssetMediaKind MediaKind => AssetMediaKind.Video;

    public PixelDimensions Dimensions { get; }

    public CodecIdentifier Codec { get; }

    public FrameRate FrameRate { get; }

    public int BitDepth { get; }

    public BitRate? BitRate { get; }
}
