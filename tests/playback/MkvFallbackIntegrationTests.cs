using System.Diagnostics;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Playback.Contracts;
using Hispania.Playback.Application;
using Hispania.Server;
using Hispania.Transcoding.FFmpeg;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class MkvFallbackIntegrationTests
{
    [Theory]
    [InlineData("libx264", "h264")]
    [InlineData("libx265", "hevc")]
    public async Task MkvWithAc3ProducesPlayableMp4WithoutChangingSource(string encoder, string inputVideoCodec)
    {
        Assert.True(ExecutableAvailable("ffmpeg"), "The real FFmpeg integration test requires ffmpeg on PATH.");
        Assert.True(ExecutableAvailable("ffprobe"), "The real FFmpeg integration test requires ffprobe on PATH.");

        var directory = Path.Combine(Path.GetTempPath(), $"hispania-mkv-fallback-{Guid.NewGuid():N}");
        var movieDirectory = Path.Combine(directory, "Movie (2026)");
        var cacheDirectory = Path.Combine(directory, "cache");
        Directory.CreateDirectory(movieDirectory);
        try
        {
            var sourcePath = Path.Combine(movieDirectory, "Movie (2026).mkv");
            using (var generator = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                ArgumentList = { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "1", "-c:v", encoder, "-pix_fmt", "yuv420p", "-c:a", "ac3", sourcePath }
            }))
            {
                Assert.NotNull(generator);
                var error = await generator.StandardError.ReadToEndAsync();
                await generator.WaitForExitAsync();
                Assert.True(generator.ExitCode == 0, error);
            }

            var original = new FileInfo(sourcePath);
            var originalLastWrite = original.LastWriteTimeUtc;
            var file = new LibraryFileReference("movies", "Movie (2026)/Movie (2026).mkv");
            var candidate = new DiscoveredMediaCandidate(file, original.Length, originalLastWrite);
            var probe = new ProbeSucceeded(new ProbeResult("matroska", TimeSpan.FromSeconds(1),
            [
                new VideoStreamObservation(0, inputVideoCodec, 64, 64, new ObservedRational(24, 1), 8, null),
                new AudioStreamObservation(1, "ac3", 48000, 1, null, null)
            ]));
            var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(candidate, probe, new ImportedPlayableMovieIdentityFactory().Create())).Entry;
            var catalog = new InMemoryMediaCatalog();
            catalog.Upsert(entry);
            var accessor = new LocalFileSystemSourceContentAccessor(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", directory)]));
            Directory.CreateDirectory(cacheDirectory);
            var abandonedPartial = Path.Combine(cacheDirectory, "abandoned.partial.mp4");
            await File.WriteAllBytesAsync(abandonedPartial, [1, 2, 3]);
            File.SetLastWriteTimeUtc(abandonedPartial, DateTime.UtcNow.AddHours(-6));
            var brokenTranscoder = new FfmpegCompatibleMediaTranscoder(catalog, accessor, Path.Combine(directory, "missing-ffmpeg"), cacheDirectory);
            Assert.IsType<CompatibleTranscodeUnavailable>(await brokenTranscoder.CreateCompatibleRepresentationAsync(entry.Id));
            Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.mp4"));
            Assert.False(File.Exists(abandonedPartial));
            var transcoder = new FfmpegCompatibleMediaTranscoder(catalog, accessor, "ffmpeg", cacheDirectory);

            var outputResult = await transcoder.CreateCompatibleRepresentationAsync(entry.Id);
            var output = Assert.IsType<CompatibleTranscodeReady>(outputResult);
            var outputPath = Path.Combine(cacheDirectory, output.Content.Value + ".mp4");

            Assert.NotNull(outputPath);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
            Assert.Equal(original.Length, new FileInfo(sourcePath).Length);
            Assert.Equal(originalLastWrite, new FileInfo(sourcePath).LastWriteTimeUtc);
            Assert.Equal(output.Content, Assert.IsType<CompatibleTranscodeReady>(await transcoder.CreateCompatibleRepresentationAsync(entry.Id)).Content);
            using var inspect = Process.Start(new ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                ArgumentList = { "-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", outputPath }
            });
            Assert.NotNull(inspect);
            var codecs = await inspect.StandardOutput.ReadToEndAsync();
            await inspect.WaitForExitAsync();
            Assert.Equal(0, inspect.ExitCode);
            Assert.Contains("h264", codecs);
            Assert.Contains("aac", codecs);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IMediaCatalogReader>(catalog);
            builder.Services.AddSingleton<ISourceContentAccessor>(accessor);
            builder.Services.AddSingleton<IPlaybackPlanner, DirectPlayPlanner>();
            builder.Services.AddSingleton<IDirectPlayExecutor, DirectPlayExecutor>();
            builder.Services.AddSingleton<ICompatibleMediaTranscoderV1>(transcoder);
            builder.Services.AddSingleton<ITranscodedContentStore>(transcoder);
            await using var app = builder.Build();
            app.MapDirectPlayEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{entry.Id.Value}/stream");
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
            using var response = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(1024, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static bool ExecutableAvailable(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, ArgumentList = { "-version" } });
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
