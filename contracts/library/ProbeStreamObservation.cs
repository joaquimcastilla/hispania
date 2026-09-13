namespace Hispania.Library.Contracts;

public abstract record ProbeStreamObservation
{
    protected ProbeStreamObservation(int streamIndex, string? codecCode)
    {
        if (streamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), "A probe stream index cannot be negative.");
        }

        StreamIndex = streamIndex;
        CodecCode = codecCode;
    }

    public int StreamIndex { get; }

    public string? CodecCode { get; }
}

public sealed record VideoStreamObservation : ProbeStreamObservation
{
    public VideoStreamObservation(
        int streamIndex,
        string? codecCode,
        int? width,
        int? height,
        ObservedRational? frameRate,
        int? bitDepth,
        long? bitRate)
        : base(streamIndex, codecCode)
    {
        Width = width;
        Height = height;
        FrameRate = frameRate;
        BitDepth = bitDepth;
        BitRate = bitRate;
    }

    public int? Width { get; }

    public int? Height { get; }

    public ObservedRational? FrameRate { get; }

    public int? BitDepth { get; }

    public long? BitRate { get; }
}

public sealed record AudioStreamObservation : ProbeStreamObservation
{
    public AudioStreamObservation(
        int streamIndex,
        string? codecCode,
        int? sampleRateHertz,
        int? channelCount,
        int? bitDepth,
        long? bitRate)
        : base(streamIndex, codecCode)
    {
        SampleRateHertz = sampleRateHertz;
        ChannelCount = channelCount;
        BitDepth = bitDepth;
        BitRate = bitRate;
    }

    public int? SampleRateHertz { get; }

    public int? ChannelCount { get; }

    public int? BitDepth { get; }

    public long? BitRate { get; }
}

public sealed record UnsupportedStreamObservation : ProbeStreamObservation
{
    public UnsupportedStreamObservation(int streamIndex, string? observedKind, string? codecCode)
        : base(streamIndex, codecCode)
    {
        ObservedKind = observedKind;
    }

    public string? ObservedKind { get; }
}

public sealed record ObservedRational(long? Numerator, long? Denominator);
