namespace Agentstration.Web.Hosting;

internal static class BootstrapAdministrationAccess
{
    // This allowlist only bypasses the startup fence. Normal endpoint authentication,
    // authorization and scope checks still run; execution endpoints stay fenced.
    internal static bool IsAdministrationPath(PathString path) =>
        new[] { "/startup/progress", "/modelproviders", "/modelprofiles", "/extensions", "/tenants", "/workspaces", "/bootstrap",
            "/api/modelproviders", "/api/modelprofiles", "/api/models", "/api/extensions", "/api/extensionregistrations", "/api/aep/enrollments",
            "/api/parameters", "/api/secrets",
            "/api/auth", "/api/resource-scopes", "/api/resource-access-policies",
            "/api/identity", "/api/authorization", "/api/tenants", "/api/workspaces", "/api/principals",
            "/api/bootstrap", "/api/internal/bff", "/api/notifications", "/api/navigation", "/api/console", "/_blazor" }
            .Any(prefix => path.StartsWithSegments(prefix));

    internal static bool IsLoginOrAsset(PathString path) =>
        path == "/" || path == "/auth.css" || path == "/resource-name-fields.js" || (path.Value?.StartsWith("/Agentstration.Web.", StringComparison.Ordinal) == true
            && path.Value.EndsWith(".styles.css", StringComparison.Ordinal)) || new[] { "/account", "/Account", "/auth", "/login", "/logout", "/signin-oidc", "/signout-callback-oidc", "/_framework", "/_content", "/css", "/js", "/images", "/favicon.ico" }
            .Any(prefix => path.StartsWithSegments(prefix));
}
