namespace Hispania.Media.Domain.TechnicalRepresentations;

public sealed record ContainerExposure
{
    public ContainerExposure(ContainerFormat containerFormat, StreamLocator streamLocator)
    {
        ContainerFormat = containerFormat ?? throw new DomainValidationException("container-exposure.format.required", "A container format is required.");
        StreamLocator = streamLocator ?? throw new DomainValidationException("container-exposure.stream-locator.required", "A stream locator is required.");
    }

    public ContainerFormat ContainerFormat { get; }

    public StreamLocator StreamLocator { get; }
}
