using System.Text.Json;
using Hispania.Transcoding.Worker.Contracts;

if (args.Length != 1 || args[0] != "--protocol-v1") return 64;
var request = JsonSerializer.Deserialize<FfmpegWorkerRequestV1>(Environment.GetEnvironmentVariable("HISPANIA_FFMPEG_REQUEST_V1") ?? string.Empty);
if (request is null || !request.IsValid) return 64;
var cache = Environment.GetEnvironmentVariable("HISPANIA_FFMPEG_CACHE")!;
var partial = Path.Combine(cache, request.CacheKey + ".fake.partial.mp4");
Directory.CreateDirectory(cache);
await using var input = Console.OpenStandardInput();
var marker = new byte[1];
if (await input.ReadAsync(marker.AsMemory()) != 1 || marker[0] != 1) return 64;
var mode = Environment.GetEnvironmentVariable("HISPANIA_FAKE_WORKER_MODE") ?? "success";
if (mode == "wait")
{
    await File.WriteAllBytesAsync(partial, [1, 2, 3]);
    await Task.Delay(TimeSpan.FromMinutes(10));
}
if (mode == "delay") await Task.Delay(300);

if (mode == "crash")
{
    await File.WriteAllBytesAsync(partial, [1, 2, 3]);
    return 73;
}

if (mode == "oversized-response")
{
    Console.Out.Write(new string('x', 4097));
    return 0;
}

var remaining = request.SourceLength;
var buffer = new byte[64 * 1024];
while (remaining > 0)
{
    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
    if (read == 0) return 65;
    remaining -= read;
}
await File.WriteAllBytesAsync(partial, [1, 2, 3, 4]);
var outputPath = Path.Combine(cache, request.CacheKey + ".mp4");
if (mode == "publication-failure") Directory.CreateDirectory(outputPath);
File.Move(partial, outputPath);
var response = new FfmpegWorkerResponseV1(1, true, request.CacheKey, null) { CorrelationId = request.CorrelationId };
Console.Out.Write(JsonSerializer.Serialize(response));
return 0;
