namespace Hispania.Library.Contracts;

public sealed record ProbeResult
{
    private readonly ProbeStreamObservation[] _streams;

    public ProbeResult(string? containerFormatCode, TimeSpan? observedDuration, IEnumerable<ProbeStreamObservation>? streams)
    {
        ContainerFormatCode = containerFormatCode;
        ObservedDuration = observedDuration;
        _streams = streams?.ToArray() ?? [];

        if (_streams.Any(stream => stream is null))
        {
            throw new ArgumentException("A probe stream collection cannot contain null values.", nameof(streams));
        }
    }

    public string? ContainerFormatCode { get; }

    public TimeSpan? ObservedDuration { get; }

    public IReadOnlyCollection<ProbeStreamObservation> Streams => _streams.ToArray();
}
