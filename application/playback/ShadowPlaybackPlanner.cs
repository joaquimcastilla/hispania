using Hispania.Media.Domain.Identity;
using Hispania.Playback.Contracts;

namespace Hispania.Playback.Application;

/// <summary>Shadow decorator for a deterministic, side-effect-free playback planning operation.</summary>
/// <remarks>Only implementations reviewed as pure reads may be passed as the shadow variant. Runtime cannot sandbox native code.</remarks>
public sealed class ShadowPlaybackPlanner(string activeImplementationId, IPlaybackPlanner active, string shadowImplementationId, IPlaybackPlanner shadow, Action<ShadowComparison<PlaybackPlanningResult>>? observe = null) : IPlaybackPlanner
{
    public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId)
    {
        var activeResult = active.Plan(mediaEntityId);
        PlaybackPlanningResult? shadowResult = null;
        Exception? failure = null;
        try { shadowResult = shadow.Plan(mediaEntityId); }
        catch (Exception exception) { failure = exception; }
        observe?.Invoke(new ShadowComparison<PlaybackPlanningResult>(activeImplementationId, shadowImplementationId, activeResult, shadowResult, failure));
        return activeResult;
    }
}

public sealed record ShadowComparison<TResult>(string ActiveImplementationId, string ShadowImplementationId, TResult ActiveResult, TResult? ShadowResult, Exception? ShadowFailure);
