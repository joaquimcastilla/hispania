namespace Hispania.ModuleSystem.Contracts;

/// <summary>Experimental serialized Web schema; package bytes remain authenticated by Files.</summary>
public static class WebContributionSchema
{
    public const int Generation = 1;
    public const string Path = "web/contributions.json";
    public const string Capability = "hispania.web.contributions";
    public const string Requirement = Capability + "@1.0";
}

public sealed record WebContributionDocument(int SchemaGeneration, IReadOnlyList<WebPageContribution> Contributions);
public sealed record WebPageContribution(string Id, string Route, string Title, string Permission,
    WebNavigation Navigation, IReadOnlyList<WebComponent> Components);
public sealed record WebNavigation(string Icon, string Section, int Order, string? Asset = null);
public sealed record WebComponent(string Type, string Text, string? Operation = null);
public sealed record WebContribution(string PackageId, string PackageVersion, string ContentHash, WebPageContribution Page);
public sealed record WebContributionDiagnostic(string PackageId, string State, string Reason, int Count,
    string? ContributionId = null, string? Route = null, string Type = "page", string? PackageVersion = null);
public sealed record WebContributionSnapshot(IReadOnlyList<WebContribution> Contributions, IReadOnlyList<WebContributionDiagnostic> Diagnostics);
