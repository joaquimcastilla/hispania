using System.Globalization;
using Hispania.Runtime;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Transcoding.FFmpeg;

/// <summary>Opt-in Media provider loaded by the generic worker agent.</summary>
public sealed class DistributedFfmpegCapabilityProvider : IDistributedWorkerCapabilityProviderV1
{
    public IReadOnlyCollection<IDistributedCapabilityHandlerV1> CreateHandlers(DistributedWorkerCapabilityContextV1 context)
    {
        var config = context.Configuration;
        string Get(string key, string fallback) => config.TryGetValue("DistributedWorker:" + key, out var value) ? value : fallback;
        if (!bool.TryParse(Get("Ffmpeg:Enabled", "false"), out var enabled) || !enabled) return [];
        var policy = new FfmpegWorkerPolicy();
        var maximum = int.TryParse(Get("Ffmpeg:WorkerPolicy:MaximumConcurrentJobs", policy.MaximumConcurrentJobs.ToString(CultureInfo.InvariantCulture)), out var parsed) ? parsed : policy.MaximumConcurrentJobs;
        policy = policy with { MaximumConcurrentJobs = maximum };
        var workerAssembly = Get("Ffmpeg:WorkerAssemblyPath", Path.Combine(AppContext.BaseDirectory, "ffmpeg-worker", "Hispania.FFmpeg.Worker.dll"));
        var ffmpegPath = Get("Ffmpeg:ExecutablePath", "ffmpeg");
        var cache = Get("Ffmpeg:CachePath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "remote-transcode-cache"));
        return [new DistributedFfmpegCapabilityHandler(context.CoordinatorUri, context.NodeId, context.ClientCertificate, context.CoordinatorPins, workerAssembly, ffmpegPath, cache, policy)];
    }
}
