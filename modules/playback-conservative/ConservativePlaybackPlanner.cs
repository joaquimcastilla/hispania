using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;
using Hispania.ModuleSystem.Contracts;
using Hispania.Playback.Contracts;

namespace Hispania.Playback.Conservative;

/// <summary>Read-only fallback planner: existing catalog items continue through the compatible transcoder.</summary>
public sealed class ConservativePlaybackPlanner(IMediaCatalogReader catalog) : IPlaybackPlanner, IModule, IModuleHealthCheck, IModuleExperimentDeclaration
{
    public ModuleDescriptor Descriptor { get; } = new(
        "hispania.playback.conservative-planner",
        "HISPANIA Conservative Playback Planner",
        new Version(1, 0, 0),
        [new ModuleCapability("hispania.playback.planner", new Version(1, 0), dynamicallyReplaceable: true)]);

    public IReadOnlyCollection<ModuleExperimentCapability> ExperimentCapabilities { get; } =
    [new("hispania.playback.planner", new Version(1, 0), [ModuleExperimentMode.Manual, ModuleExperimentMode.Shadow, ModuleExperimentMode.TrafficSplit], "Hispania.Library.Contracts.IMediaCatalogReader@1.0")];

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId)
    {
        ArgumentNullException.ThrowIfNull(mediaEntityId);
        return catalog.GetById(mediaEntityId) is null
            ? new PlaybackMediaNotFound(mediaEntityId)
            : new DirectPlayUnavailable(mediaEntityId);
    }
}
