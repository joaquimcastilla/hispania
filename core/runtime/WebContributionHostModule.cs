using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

/// <summary>Host-composed required-feature negotiation; no package-supplied executable renderer.</summary>
public sealed class WebContributionHostModule : IModule
{
    public ModuleDescriptor Descriptor { get; } = new("platform.web.contributions", "Declarative Web host", new Version(1, 0),
        provides: [new(WebContributionSchema.Capability, new Version(1, 0))]);
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
