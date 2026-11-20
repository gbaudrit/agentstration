using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Web.Console;
using Agentstration.Web.Security;

namespace Agentstration.Web.Hosting;

internal sealed class BootstrapStartupAdmissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, BootstrapStartupState startup, IAuthenticationAdmissionPolicy admission)
    {
        if (startup.IsReady || !startup.AdministrationReady || IsInfrastructure(context.Request.Path))
        {
            await next(context); return;
        }
        var browser = BootstrapAdministrationAccess.IsConsoleNavigation(context.Request);
        var path = context.Request.Path;
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var principal = context.Features.Get<ResolvedPrincipalFeature>();
            var allowed = principal is not null && await admission.CanSignInAsync(principal.PrincipalId, context.RequestAborted);
            if (!allowed && path != BootstrapStartupNavigation.ProgressPath)
            {
                if (browser) context.Response.Redirect(context.Request.PathBase + BootstrapStartupNavigation.ProgressUrl("/"));
                else context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.Headers.CacheControl = "no-store";
                return;
            }
            if (browser && path != BootstrapStartupNavigation.ProgressPath && !BootstrapStartupNavigation.IsRepairPath(path.Value ?? "/"))
            {
                context.Response.Redirect(context.Request.PathBase + BootstrapStartupNavigation.ProgressUrl(path + context.Request.QueryString));
                context.Response.Headers.CacheControl = "no-store";
                return;
            }
        }
        await next(context);
    }

    private static bool IsInfrastructure(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/api/auth")
        || path.StartsWithSegments("/api/internal/bff") || path == "/api/aep/enrollments/announce"
        || (path != "/" && BootstrapAdministrationAccess.IsLoginOrAsset(path));
}
