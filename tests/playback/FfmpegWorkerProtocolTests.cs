using System.Diagnostics;
using System.Text.Json;
using Hispania.Playback.Contracts;
using Hispania.Transcoding.Worker.Contracts;
using Xunit;

namespace Hispania.Playback.Tests;

public sealed class FfmpegWorkerProtocolTests
{
    [Fact]
    public async Task WorkerRejectsUnknownProtocolBeforeReadingMedia()
    {
        var workerAssembly = Path.Combine(AppContext.BaseDirectory, "Hispania.FFmpeg.Worker.dll");
        Assert.True(File.Exists(workerAssembly), $"Worker assembly was not copied to test output: {workerAssembly}");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(workerAssembly);
        start.ArgumentList.Add("--protocol-v2");
        using var worker = Process.Start(start);
        Assert.NotNull(worker);
        worker.StandardInput.Close();
        await worker.WaitForExitAsync();
        Assert.Equal(64, worker.ExitCode);
        Assert.Empty(await worker.StandardOutput.ReadToEndAsync());
    }

    [Fact]
    public void WorkerRequestRejectsPathsAndInvalidValues()
    {
        var valid = new FfmpegWorkerRequestV1(FfmpegWorkerRequestV1.ContractId, new string('a', 64), "libx264", 256);
        Assert.True(valid.IsValid);
        Assert.False((valid with { Contract = "hispania.transcoding.ffmpeg.worker@2" }).IsValid);
        Assert.False((valid with { CacheKey = "../../outside" }).IsValid);
        Assert.False((valid with { VideoCodec = "shell;command" }).IsValid);
        Assert.False((valid with { SourceLength = 0 }).IsValid);
        Assert.Equal(JsonValueKind.String, JsonDocument.Parse(JsonSerializer.Serialize(valid)).RootElement.GetProperty("CacheKey").ValueKind);
        Assert.DoesNotContain(typeof(CompatibleTranscodeReady).GetProperties(), property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }
}
