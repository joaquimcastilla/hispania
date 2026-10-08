using Microsoft.AspNetCore.Routing;

namespace Hispania.Server;

public static class SecurityEndpointInventory
{
    private static readonly string[] ReadMethods = ["GET"];
    private static readonly string[] WriteMethods = ["POST"];
    private static readonly string[] ReadWriteMethods = ["GET", "POST"];
    private static readonly string[] ShellMethods = ["GET", "HEAD"];
    // Exact exceptions: adding an endpoint cannot silently inherit a public prefix.
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["/healthz"] = "public-liveness", ["/readyz"] = "public-readiness",
        ["/api/health/live"] = "public-liveness", ["/api/health/ready"] = "public-readiness", ["/api/health"] = "public-readiness",
        ["/api/identity/login"] = "public-authentication-rate-limited",
        ["/api/authorization/bootstrap-admin"] = "one-time-bootstrap-secret-rate-limited",
        ["/api/identity/me"] = "session-handler", ["/api/identity/logout"] = "session-handler",
        ["/api/assets/{id:guid}"] = "asset-public-read-or-admin-handler",
        ["/api/assets/{id:guid}/content"] = "asset-public-read-or-admin-handler",
        ["/api/v1/artwork/{artworkReferenceId}"] = "public-artwork-reference",
        ["/api/v1/web/assets/{packageId}/{artifactHash}/{name}"] = "public-signed-active-png",
        ["/api/v1/web/contributions"] = "session-and-visible-permission-handler",
        ["/api/v1/web/contributions/{packageId}/{contributionId}/operations/{operation}"] = "session-and-contribution-and-operation-permission-handler",
        ["/internal/module-test"] = "development-only",
        ["/_test/shutdown"] = "test-secret-handler",
        ["{*path:nonfile}"] = "public-web-shell"
    };
    public sealed record EndpointPolicy(string Route, string[] Methods, string Policy, bool LegacyBearer);
    public static EndpointPolicy[] Snapshot(IEnumerable<EndpointDataSource> sources) => sources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Select(endpoint =>
    {
        var route = endpoint.RoutePattern.RawText ?? "";
        var permission = endpoint.Metadata.GetMetadata<AuthorizationEndpointFilters.PermissionPolicy>();
        var explicitPolicy = endpoint.Metadata.GetMetadata<ExplicitPolicy>();
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToArray() ?? [];
        var expectedMethods = route switch
        {
            "/api/identity/login" or "/api/identity/logout" or "/api/authorization/bootstrap-admin" or "/_test/shutdown" => WriteMethods,
            "/api/v1/web/contributions/{packageId}/{contributionId}/operations/{operation}" => ReadWriteMethods,
            "{*path:nonfile}" => ShellMethods,
            _ => ReadMethods
        };
        var exception = methods.Length > 0 && methods.All(method => expectedMethods.Contains(method, StringComparer.Ordinal)) ? Exceptions.GetValueOrDefault(route) : null;
        var policy = permission is not null ? "permission:" + permission.PermissionId : explicitPolicy?.Name ?? exception ?? "UNCLASSIFIED";
        return new EndpointPolicy(route, methods, policy, permission?.AllowLegacyAdminBearer ?? false);
    }).OrderBy(row => row.Route, StringComparer.Ordinal).ToArray();
    public sealed record ExplicitPolicy(string Name);
    public static void Validate(IEnumerable<EndpointDataSource> sources)
    {
        if (Snapshot(sources).Any(row => row.Policy == "UNCLASSIFIED")) throw new InvalidOperationException("HTTP endpoint lacks an explicit security policy; update the reviewed endpoint inventory.");
    }
}
