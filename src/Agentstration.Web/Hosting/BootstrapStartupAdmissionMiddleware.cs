using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Web.Console;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace Agentstration.Web.Hosting;

internal sealed class BootstrapStartupAdmissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, BootstrapStartupState startup, IAuthenticationAdmissionPolicy admission)
    {
        if (startup.IsReady || !startup.AdministrationReady || context.User.Identity?.IsAuthenticated != true
            || context.User.Identity.AuthenticationType == AgentstrationAuthenticationDefaults.AwpWorkerScheme)
        {
            await next(context); return;
        }
        if (context.User.Identity.AuthenticationType == AgentstrationAuthenticationDefaults.BffWorkloadScheme)
        {
            await next(context); return;
        }
        var path = context.Request.Path;
        var principal = context.Features.Get<ResolvedPrincipalFeature>();
        var allowed = principal is not null && await admission.CanSignInAsync(principal.PrincipalId, context.RequestAborted);

        // Public endpoints retain their own protocol checks. Protected endpoints,
        // including Blazor transport, never skip admission based on a path prefix.
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || path == "/logout" || path == "/api/auth/logout")
        {
            await next(context); return;
        }

        var browser = BootstrapAdministrationAccess.IsConsoleNavigation(context.Request);
        var pathBase = AuthenticationReturnUrls.Normalize(context.Request.PathBase.Value).TrimEnd('/');
        if (!allowed && path != BootstrapStartupNavigation.ProgressPath)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (browser) await Results.LocalRedirect(pathBase + BootstrapStartupNavigation.ProgressUrl("/")).ExecuteAsync(context);
            else context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (browser && path != BootstrapStartupNavigation.ProgressPath && !BootstrapStartupNavigation.IsInspectionPath(path.Value ?? "/"))
        {
            context.Response.Headers.CacheControl = "no-store";
            await Results.LocalRedirect(pathBase + BootstrapStartupNavigation.ProgressUrl(path + context.Request.QueryString)).ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
