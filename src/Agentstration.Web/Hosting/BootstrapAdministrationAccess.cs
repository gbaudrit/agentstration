namespace Agentstration.Web.Hosting;

internal static class BootstrapAdministrationAccess
{
    internal static bool IsInspectionRead(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && new[] { "/api/agents", "/api/deployments", "/api/flows", "/api/flowRuns", "/api/schemas", "/api/runtimeprofiles", "/api/tools", "/api/data-sources",
            "/api/data-source-profiles", "/api/knowledge-sources", "/api/storages", "/api/storage-types", "/api/connected-services",
            "/api/packs", "/api/resource-plans", "/api/entries", "/api/transformation-functions" }
            .Any(prefix => request.Path.StartsWithSegments(prefix));
    // This allowlist only bypasses the startup fence. Normal endpoint authentication,
    // authorization and scope checks still run; execution endpoints stay fenced.
    internal static bool IsAdministrationPath(PathString path) =>
        new[] { "/startup/progress", "/modelproviders", "/modelprofiles", "/extensions", "/tenants", "/workspaces", "/bootstrap",
            "/api/modelproviders", "/api/modelprofiles", "/api/models", "/api/extensions", "/api/extensionregistrations", "/api/aep/enrollments",
            "/api/parameters", "/api/secrets",
            "/api/auth", "/api/resource-scopes", "/api/resource-access-policies",
            "/api/identity", "/api/authorization", "/api/tenants", "/api/workspaces", "/api/principals",
            "/api/bootstrap", "/api/internal/bff", "/api/notifications", "/api/navigation", "/api/console", "/_blazor", "/hubs/flow-runs" }
            .Any(prefix => path.StartsWithSegments(prefix));

    // Let document navigations reach authentication and the post-authentication
    // redirect. API/MCP/Hub traffic remains subject to the execution fence.
    internal static bool IsConsoleNavigation(HttpRequest request) => HttpMethods.IsGet(request.Method)
        && !new[] { "/api", "/mcp", "/hubs", "/health", "/_blazor", "/_framework", "/_content" }
            .Any(prefix => request.Path.StartsWithSegments(prefix))
        && request.GetTypedHeaders().Accept?.Any(value =>
            value.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) && value.Quality is null or > 0) == true;

    internal static bool IsLoginOrAsset(PathString path) =>
        path == "/" || path == "/auth.css" || path == "/resource-name-fields.js" || (path.Value?.StartsWith("/Agentstration.Web.", StringComparison.Ordinal) == true
            && path.Value.EndsWith(".styles.css", StringComparison.Ordinal)) || new[] { "/account", "/Account", "/auth", "/login", "/logout", "/signin-oidc", "/signout-callback-oidc", "/_framework", "/_content", "/css", "/js", "/images", "/favicon.ico" }
            .Any(prefix => path.StartsWithSegments(prefix));
}
