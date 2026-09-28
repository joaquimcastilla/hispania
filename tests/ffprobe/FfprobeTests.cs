using System.Globalization;
using System.Diagnostics;
using Hispania.Library.Contracts;
using Hispania.Library.FFprobe;
using Hispania.Media.Domain.Sources;
using Xunit;

namespace Hispania.Library.FFprobe.Tests;

public sealed class FfprobeTests
{
 [Fact] public async Task RealFfprobeAcceptsFastStartMp4WhenItClosesTheInputPipeEarly()
 {
  if (!ExecutableAvailable("ffmpeg") || !ExecutableAvailable("ffprobe")) return;
  var directory = Path.Combine(Path.GetTempPath(), $"hispania-ffprobe-pipe-{Guid.NewGuid():N}");
  Directory.CreateDirectory(directory);
  var path = Path.Combine(directory, "fast-start.mp4");
  try
  {
   using (var generator = Process.Start(new ProcessStartInfo("ffmpeg")
   {
    UseShellExecute = false,
    RedirectStandardError = true,
    ArgumentList = { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24:duration=8", "-f", "lavfi", "-i", "sine=frequency=1000:duration=8", "-c:v", "mpeg4", "-q:v", "6", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-movflags", "+faststart", path }
   }))
   {
    Assert.NotNull(generator);
    var error = await generator.StandardError.ReadToEndAsync();
    await generator.WaitForExitAsync();
    Assert.True(generator.ExitCode == 0, error);
   }

   await using var input = File.OpenRead(path);
   var result = await new FfprobeProcessRunner().RunAsync(new FfprobeOptions("ffprobe"), input, CancellationToken.None);
   Assert.False(result.StartFailed);
   Assert.False(result.TimedOut);
   Assert.Equal(0, result.ExitCode);
   Assert.IsType<ProbeSucceeded>(FfprobeJsonParser.Parse(result.StandardOutput));
  }
  finally { Directory.Delete(directory, recursive: true); }
 }

 private static bool ExecutableAvailable(string name)
 {
  try
  {
   using var process = Process.Start(new ProcessStartInfo(name) { UseShellExecute = false, RedirectStandardError = true, ArgumentList = { "-version" } });
   if (process is null) return false;
   process.WaitForExit(5000);
   return process.ExitCode == 0;
  }
  catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException) { return false; }
 }

 [Fact] public void ParsesH264AacMp4() { var outcome = FfprobeJsonParser.Parse(Fixture("mp4-h264-aac.json")); var result = Assert.IsType<ProbeSucceeded>(outcome).Result; Assert.Equal(TimeSpan.FromTicks(1_234_560_000), result.ObservedDuration); var video = Assert.IsType<VideoStreamObservation>(result.Streams.First()); Assert.Equal(0, video.StreamIndex); Assert.Equal("h264", video.CodecCode); Assert.Equal(1920, video.Width); Assert.Equal(1080, video.Height); Assert.Equal(new ObservedRational(24000,1001), video.FrameRate); var audio = Assert.IsType<AudioStreamObservation>(result.Streams.Skip(1).Single()); Assert.Equal(1,audio.StreamIndex); Assert.Equal("aac",audio.CodecCode); Assert.Equal(48000,audio.SampleRateHertz); Assert.Equal(2,audio.ChannelCount); Assert.Null(audio.BitRate); Assert.Null(audio.BitDepth); }
 [Fact] public void OptionalFieldsCanBeAbsent() { var result = Assert.IsType<ProbeSucceeded>(FfprobeJsonParser.Parse(Fixture("optional-absent.json"))).Result; Assert.Null(result.ObservedDuration); Assert.Null(Assert.IsType<VideoStreamObservation>(result.Streams.First()).FrameRate); }
 [Fact] public void ZeroAudioBitDepthIsTreatedAsUnavailable() { var json = "{\"format\":{\"format_name\":\"mp4\"},\"streams\":[{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"bits_per_sample\":0}]}"; var audio = Assert.IsType<AudioStreamObservation>(Assert.IsType<ProbeSucceeded>(FfprobeJsonParser.Parse(json)).Result.Streams.Single()); Assert.Null(audio.BitDepth); }
 [Fact] public void PreservesUnknownStreams() { var stream = Assert.IsType<UnsupportedStreamObservation>(Assert.IsType<ProbeSucceeded>(FfprobeJsonParser.Parse(Fixture("unsupported-stream.json"))).Result.Streams.Single()); Assert.Equal("data",stream.ObservedKind); }
 [Theory][InlineData("matroska,webm")][InlineData("avi")] public void PreservesNonMp4ContainersForTheLibraryMapper(string container) { var json = "{\"format\":{\"format_name\":\"" + container + "\"},\"streams\":[]}"; Assert.Equal(container, Assert.IsType<ProbeSucceeded>(FfprobeJsonParser.Parse(json)).Result.ContainerFormatCode); }
 [Theory][InlineData("1/0")][InlineData("wat")][InlineData("12/3/4")] public void InvalidRationalFailsOutput(string rational) { var json = "{\"format\":{\"format_name\":\"mp4\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"avg_frame_rate\":\"" + rational + "\"}]}"; Assert.Equal(ProbeFailureKind.InvalidOutput, Assert.IsType<ProbeFailed>(FfprobeJsonParser.Parse(json)).Failure.Kind); }
 [Fact] public void InvalidJsonFailsOutput() => Assert.Equal(ProbeFailureKind.InvalidOutput, Assert.IsType<ProbeFailed>(FfprobeJsonParser.Parse(Fixture("invalid.json"))).Failure.Kind);
 [Fact] public void NumericParsingIsInvariant() { var prior = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = new CultureInfo("es-ES"); var result = Assert.IsType<ProbeSucceeded>(FfprobeJsonParser.Parse(Fixture("mp4-h264-aac.json"))).Result; Assert.Equal(TimeSpan.FromTicks(1_234_560_000),result.ObservedDuration); } finally { CultureInfo.CurrentCulture = prior; } }
 [Theory][InlineData(0,ProbeFailureKind.TimedOut)][InlineData(1,ProbeFailureKind.ProcessFailed)][InlineData(2,ProbeFailureKind.CorruptMedia)][InlineData(3,ProbeFailureKind.ProcessFailed)] public async Task ProcessOutcomesAreMappedWithoutFfprobe(int scenario, ProbeFailureKind expected) { var process = scenario switch { 0 => new FfprobeProcessResult(null,"","",true,false,false), 1 => new FfprobeProcessResult(null,"","",false,true,false), 2 => new FfprobeProcessResult(1,"","Invalid data found",false,false,false), _ => new FfprobeProcessResult(1,"","missing executable",false,false,true) }; var source = new FakeSourceAccess(); var probe = new FfprobeMediaProbe(source, new FfprobeOptions("ffprobe"), new FakeRunner(process)); var outcome = await probe.ProbeAsync(new DiscoveredMediaCandidate(new LibraryFileReference("movies","film.mp4"), source: source.Source)); Assert.Equal(expected, Assert.IsType<ProbeFailed>(outcome).Failure.Kind); Assert.Equal(1, source.OpenCount); }
 private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",name));
 private sealed class FakeRunner(FfprobeProcessResult result) : IFfprobeProcessRunner { public Task<FfprobeProcessResult> RunAsync(FfprobeOptions options, Stream input, CancellationToken cancellationToken) => Task.FromResult(result); }
 private sealed class FakeSourceAccess : IMediaSourceContentAccessV1 { public int OpenCount { get; private set; } public Hispania.Media.Domain.Sources.MediaSource Source { get; } = new(Hispania.Media.Domain.Identity.MediaSourceId.New(), LibrarySourceLocator.From(new LibraryFileReference("movies", "film.mp4"))); public ValueTask<MediaSourceContentResult> OpenMediaSourceReadAsync(Hispania.Media.Domain.Sources.MediaSource source, CancellationToken cancellationToken = default) { OpenCount++; return ValueTask.FromResult<MediaSourceContentResult>(new MediaSourceContentOpened(new ReadOnlyMediaSourceContent(new MemoryStream([1, 2, 3]), new MediaSourceContentFacts(3, DateTimeOffset.UnixEpoch)))); } }
}
