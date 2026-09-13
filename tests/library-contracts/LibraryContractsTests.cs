using Hispania.Library.Contracts;
using Xunit;

namespace Hispania.Library.Contracts.Tests;

public sealed class LibraryContractsTests
{
    [Fact]
    public void ImportKeyNormalizesSlashesAndUsesOrdinalEquality()
    {
        var normalized = new ImportKey("movies", "Films\\Arrival.mp4");

        Assert.Equal("Films/Arrival.mp4", normalized.NormalizedRelativePath);
        Assert.Equal(new ImportKey("movies", "Films/Arrival.mp4"), normalized);
        Assert.NotEqual(new ImportKey("Movies", "Films/Arrival.mp4"), normalized);
        Assert.NotEqual(new ImportKey("movies", "films/Arrival.mp4"), normalized);
    }

    [Theory]
    [InlineData("/absolute.mp4")]
    [InlineData("C:\\absolute.mp4")]
    [InlineData(".")]
    [InlineData("films/../movie.mp4")]
    [InlineData("films//movie.mp4")]
    [InlineData("films/\u0001movie.mp4")]
    public void ImportKeyRejectsUnsafeRelativePaths(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => new ImportKey("movies", relativePath));
    }

    [Fact]
    public void LibraryFileReferenceNormalizesSeparatorsWithoutExposingAnAbsolutePath()
    {
        var reference = new LibraryFileReference("movies", "Films\\Arrival.mp4");

        Assert.Equal("movies", reference.RootKey);
        Assert.Equal("Films/Arrival.mp4", reference.RelativePath);
    }

    [Theory]
    [InlineData("../escape.mp4")]
    [InlineData("films/../../escape.mp4")]
    [InlineData("/absolute.mp4")]
    [InlineData("C:\\absolute.mp4")]
    [InlineData("films//movie.mp4")]
    public void LibraryFileReferenceRejectsPathsThatCanEscapeOrAreNotCanonical(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => new LibraryFileReference("movies", relativePath));
    }

    [Fact]
    public void CandidateAllowsUnknownFileFactsButRejectsNegativeKnownLength()
    {
        var candidate = new DiscoveredMediaCandidate(new LibraryFileReference("movies", "movie.mp4"));

        Assert.Null(candidate.ByteLength);
        Assert.Null(candidate.LastModifiedUtc);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DiscoveredMediaCandidate(new LibraryFileReference("movies", "movie.mp4"), -1));
    }

    [Fact]
    public void ProbeObservationsPreserveUnknownAndTechnicallyInvalidValuesForLaterInterpretation()
    {
        var knownFrameRate = new ObservedRational(24000, 1001);
        var unknownFrameRate = new ObservedRational(null, null);
        var result = new ProbeResult(
            containerFormatCode: null,
            observedDuration: TimeSpan.FromSeconds(-1),
            streams:
            [
                new VideoStreamObservation(0, null, 0, -1, knownFrameRate, null, -1),
                new AudioStreamObservation(1, "aac", null, null, null, null),
                new UnsupportedStreamObservation(2, "data", null)
            ]);

        Assert.Equal(3, result.Streams.Count);
        Assert.Equal(knownFrameRate, Assert.IsType<VideoStreamObservation>(result.Streams.First()).FrameRate);
        Assert.Equal(new ObservedRational(null, null), unknownFrameRate);
    }

    [Fact]
    public void ProbeStreamIndexRemainsAStructuralInvariant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AudioStreamObservation(-1, "aac", 48000, 2, null, null));
    }

    [Fact]
    public void ProbeInterfaceDoesNotExposeMediaDomainTypes()
    {
        var probeTypes = typeof(IMediaProbe).GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType));

        Assert.DoesNotContain(probeTypes, type => type.Assembly.GetName().Name == "Hispania.Media.Domain");
    }

    [Fact]
    public void ProbeOutcomesRepresentSuccessAndExpectedFailuresWithoutExceptions()
    {
        ProbeOutcome success = new ProbeSucceeded(new ProbeResult("mp4", TimeSpan.FromMinutes(90), []));
        ProbeOutcome failure = new ProbeFailed(new ProbeFailure(ProbeFailureKind.UnsupportedFormat, "The observed container is not supported."));

        Assert.IsType<ProbeSucceeded>(success);
        Assert.IsType<ProbeFailed>(failure);
    }
}
