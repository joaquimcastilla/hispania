using Hispania.Media.Domain.Identity;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Microsoft.AspNetCore.Http;

namespace Hispania.Server;

/// <summary>HTTP composition adapter; application and module contracts remain independent of ASP.NET.</summary>
public sealed class ExperimentRoutedPlaybackPlanner(PlaybackExperimentService experiments, HotSwappablePlaybackPlanner defaultPlanner,
    IHttpContextAccessor contextAccessor) : IPlaybackPlanner
{
    public PlaybackPlanningResult Plan(MediaEntityId mediaEntityId) =>
        experiments.PlanForCurrentRequest(mediaEntityId, defaultPlanner, contextAccessor.HttpContext);
}
