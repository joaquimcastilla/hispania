using Hispania.Media.Domain.Assets;

namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record AudioTechnicalProfile : TechnicalProfile
{
    public AudioTechnicalProfile(CodecIdentifier codec, int sampleRateHertz, int channelCount, int? bitDepth = null, BitRate? bitRate = null)
    {
        Codec = codec ?? throw new DomainValidationException("audio-technical-profile.codec.required", "An audio codec is required.");

        if (sampleRateHertz <= 0)
        {
            throw new DomainValidationException("audio-technical-profile.sample-rate.invalid", "Audio sample rate must be positive.");
        }

        if (channelCount <= 0)
        {
            throw new DomainValidationException("audio-technical-profile.channel-count.invalid", "Audio channel count must be positive.");
        }

        if (bitDepth is <= 0)
        {
            throw new DomainValidationException("audio-technical-profile.bit-depth.invalid", "Audio bit depth must be positive when declared.");
        }

        SampleRateHertz = sampleRateHertz;
        ChannelCount = channelCount;
        BitDepth = bitDepth;
        BitRate = bitRate;
    }

    public override AssetMediaKind MediaKind => AssetMediaKind.Audio;

    public CodecIdentifier Codec { get; }

    public int SampleRateHertz { get; }

    public int ChannelCount { get; }

    public int? BitDepth { get; }

    public BitRate? BitRate { get; }
}
