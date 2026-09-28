using System.Diagnostics;
using System.Text.Json;
using Hispania.Transcoding.Worker.Contracts;

if (args.Length != 1 || args[0] != "--protocol-v1") return 64;
var requestJson = Environment.GetEnvironmentVariable("HISPANIA_FFMPEG_REQUEST_V1");
FfmpegWorkerRequestV1? request;
try { request = JsonSerializer.Deserialize<FfmpegWorkerRequestV1>(requestJson ?? string.Empty); }
catch (JsonException) { request = null; }
var ffmpeg = Environment.GetEnvironmentVariable("HISPANIA_FFMPEG_PATH");
var cache = Environment.GetEnvironmentVariable("HISPANIA_FFMPEG_CACHE");
if (request is null || !request.IsValid || string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(cache)) return 64;
var maxOutputBytesText = Environment.GetEnvironmentVariable("HISPANIA_FFMPEG_MAX_OUTPUT_BYTES");
if (!long.TryParse(maxOutputBytesText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maxOutputBytes) || maxOutputBytes <= 0) return 64;

var response = new FfmpegWorkerResponseV1(1, false, null, "transcode_failed") { CorrelationId = request.CorrelationId };
var partialPath = Path.Combine(cache, request.CacheKey + "." + Guid.NewGuid().ToString("N") + ".partial.mp4");
try
{
    Directory.CreateDirectory(cache);
    var input = Console.OpenStandardInput();
    var marker = new byte[1];
    if (await input.ReadAsync(marker.AsMemory()) != 1 || marker[0] != 1) throw new InvalidDataException("Missing V1 process start marker.");
    var finalPath = Path.Combine(cache, request.CacheKey + ".mp4");
    if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
    {
        await DrainExactlyAsync(input, request.SourceLength);
        response = new FfmpegWorkerResponseV1(1, true, request.CacheKey, null) { CorrelationId = request.CorrelationId };
        Console.Out.Write(JsonSerializer.Serialize(response));
        return 0;
    }
    var start = new ProcessStartInfo(ffmpeg)
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
    };
    foreach (var argument in new[] { "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-c:v", request.VideoCodec }) start.ArgumentList.Add(argument);
    if (request.VideoCodec != "copy") foreach (var argument in new[] { "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p" }) start.ArgumentList.Add(argument);
    foreach (var argument in new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-fs", maxOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "mp4", partialPath }) start.ArgumentList.Add(argument);
    using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
    if (!process.Start()) throw new InvalidOperationException("Could not start FFmpeg.");
    var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    await using (var ffmpegInput = process.StandardInput.BaseStream)
    {
        var remaining = request.SourceLength;
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) throw new EndOfStreamException("Host closed the media stream before the declared length.");
            await ffmpegInput.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
        await ffmpegInput.FlushAsync();
    }
    await process.WaitForExitAsync();
    await stderr;
    var outputLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
    if (process.ExitCode == 0 && outputLength > 0 && outputLength <= maxOutputBytes)
    {
        try
        {
            File.Move(partialPath, finalPath);
            response = new FfmpegWorkerResponseV1(1, true, request.CacheKey, null) { CorrelationId = request.CorrelationId };
        }
        catch (IOException) when (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
        {
            response = new FfmpegWorkerResponseV1(1, true, request.CacheKey, null) { CorrelationId = request.CorrelationId };
        }
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    response = new FfmpegWorkerResponseV1(1, false, null, ex is EndOfStreamException ? "input_truncated" : "transcode_failed") { CorrelationId = request.CorrelationId };
}
finally
{
    try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch (IOException) { }
}

Console.Out.Write(JsonSerializer.Serialize(response));
return response.Success ? 0 : 1;

static async Task DrainExactlyAsync(Stream input, long count)
{
    var buffer = new byte[64 * 1024];
    while (count > 0)
    {
        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)));
        if (read == 0) throw new EndOfStreamException("Host closed the media stream before the declared length.");
        count -= read;
    }
}
