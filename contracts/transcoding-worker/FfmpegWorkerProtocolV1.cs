namespace Hispania.Transcoding.Worker.Contracts;

/// <summary>Versioned host/worker protocol. Media bytes travel separately over redirected stdin.</summary>
public sealed record FfmpegWorkerRequestV1(string Contract, string CacheKey, string VideoCodec, long SourceLength)
{
    public const string ContractId = "hispania.transcoding.ffmpeg.worker@1";
    public string? CorrelationId { get; init; }
    public bool IsValid => Contract == ContractId && CacheKey is { Length: 64 } && CacheKey.All(Uri.IsHexDigit) &&
        VideoCodec is "copy" or "libx264" && SourceLength > 0;
}

public sealed record FfmpegWorkerResponseV1(int ProtocolVersion, bool Success, string? ContentKey, string? FailureCode)
{
    public string? CorrelationId { get; init; }
}
