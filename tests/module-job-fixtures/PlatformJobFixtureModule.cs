using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Jobs;

namespace Hispania.ModuleJob.Fixtures;

public sealed class PlatformJobFixtureModule : IModule, IPlatformJobContributionV1
{
    public const string ModuleId = "hispania.test.platform-job";
    public const string CapabilityId = "hispania.platform.job-contribution";
    public const string JobType = "hispania.test.module-work";
    public ModuleDescriptor Descriptor { get; } = new(ModuleId, "Test Platform Job module", new Version(1, 0, 0),
        [new ModuleCapability(CapabilityId, new Version(1, 0))], []);

    public ValueTask StartAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async Task<PlatformJobResultMetadataV1?> ExecuteAsync(PlatformJobContributionRequestV1 request, CancellationToken cancellationToken)
    {
        if (request.JobType != JobType) throw new InvalidDataException("Unexpected fixture job type.");
        var payload = ReadPayload(request.PayloadJson);
        if (payload.MarkerPath is not null) await File.WriteAllTextAsync(payload.MarkerPath, "started", cancellationToken);
        if (payload.DelayMilliseconds > 0) await Task.Delay(payload.DelayMilliseconds, cancellationToken);
        if (payload.Fail) throw new InvalidOperationException("Fixture handler failure.");
        return new("text/plain", null, new Dictionary<string, string> { ["value"] = payload.Value });
    }

    public sealed record FixturePayload(string Value, int DelayMilliseconds = 0, string? MarkerPath = null, bool Fail = false);

    private static FixturePayload ReadPayload(string payloadJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new(root.GetProperty("value").GetString()!,
            root.TryGetProperty("delayMilliseconds", out var delay) ? delay.GetInt32() : 0,
            root.TryGetProperty("markerPath", out var marker) && marker.ValueKind != System.Text.Json.JsonValueKind.Null ? marker.GetString() : null,
            root.TryGetProperty("fail", out var fail) && fail.GetBoolean());
    }

}

public sealed class PlatformJobFixtureModuleV2 : IModule, IPlatformJobContributionV1
{
    public ModuleDescriptor Descriptor { get; } = new(PlatformJobFixtureModule.ModuleId, "Test Platform Job module", new Version(2, 0, 0),
        [new ModuleCapability(PlatformJobFixtureModule.CapabilityId, new Version(1, 0))], []);
    public ValueTask StartAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public async Task<PlatformJobResultMetadataV1?> ExecuteAsync(PlatformJobContributionRequestV1 request, CancellationToken cancellationToken)
    {
        using var document = System.Text.Json.JsonDocument.Parse(request.PayloadJson);
        var payload = document.RootElement;
        var value = payload.GetProperty("value").GetString()!;
        var delayMilliseconds = payload.TryGetProperty("delayMilliseconds", out var delay) ? delay.GetInt32() : 0;
        var markerPath = payload.TryGetProperty("markerPath", out var marker) && marker.ValueKind != System.Text.Json.JsonValueKind.Null ? marker.GetString() : null;
        if (markerPath is not null) await File.WriteAllTextAsync(markerPath, "started-v2", cancellationToken);
        if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
        return new("text/plain", null, new Dictionary<string, string> { ["value"] = "v2:" + value });
    }
}
