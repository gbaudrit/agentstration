using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Hosting;

public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public const string WorkspaceCookie = "agentstration.workspace";
    public const string ConsoleContextCookie = "agentstration.context";
    public const string ConsoleContextHeader = "X-Agentstration-Context";

    public async Task InvokeAsync(
        HttpContext httpContext,
        ICurrentRequestContext current,
        IRequestContextScopeFactory scopeFactory,
        IdentityExperienceService experience)
    {
        if (!current.IsInitialized)
        {
            await next(httpContext);
            return;
        }

        IDisposable? workspaceScope = null;
        try
        {
            if (httpContext.Request.Cookies.TryGetValue(WorkspaceCookie, out var rawWorkspace)
                && Guid.TryParse(rawWorkspace, out var workspaceId)
                && workspaceId != current.Current.WorkspaceId)
            {
                var selectedWorkspace = await experience.ValidateWorkspaceSelectionAsync(
                    workspaceId, httpContext.RequestAborted);
                workspaceScope = scopeFactory.Push(selectedWorkspace);
            }

            if (httpContext.Request.Path.StartsWithSegments("/api/identity/context")
                || httpContext.Request.Path.StartsWithSegments("/bff/identity/context")
                || !TryGetConsoleScope(httpContext.Request, out var rawScopeRef, out var persistSelection,
                    out var clearInvalidSelection))
            {
                await next(httpContext);
                return;
            }

            var principalId = httpContext.Features.Get<ResolvedPrincipalFeature>()?.PrincipalId;
            if (principalId is null)
            {
                await next(httpContext);
                return;
            }
            ConsoleContextSelection selection;
            ResourceScopeRef scopeRef;
            try
            {
                scopeRef = ResourceScopeRef.Parse(rawScopeRef);
                selection = await experience.ValidateConsoleContextSelectionAsync(
                    principalId.Value, scopeRef, httpContext.RequestAborted);
            }
            catch (Exception exception) when (exception is AuthorizationDeniedException or ArgumentException)
            {
                if (clearInvalidSelection)
                {
                    httpContext.Response.Cookies.Delete(ConsoleContextCookie);
                    httpContext.Response.Cookies.Delete(WorkspaceCookie);
                }
                await next(httpContext);
                return;
            }

            if (persistSelection
                && !string.Equals(httpContext.Request.Cookies[ConsoleContextCookie], scopeRef.Value,
                    StringComparison.Ordinal))
            {
                var options = new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    Path = "/",
                    SameSite = SameSiteMode.Strict,
                    Secure = httpContext.Request.IsHttps,
                    MaxAge = TimeSpan.FromDays(30)
                };
                httpContext.Response.Cookies.Append(ConsoleContextCookie, scopeRef.Value, options);
                httpContext.Response.Cookies.Append(WorkspaceCookie,
                    selection.OperationalContext.WorkspaceId.ToString("D"), options);
            }

            using var consoleScope = selection.Kind switch
            {
                IdentityConsoleContextKind.Instance => scopeFactory.PushInstance(selection.OperationalContext),
                IdentityConsoleContextKind.Tenant => scopeFactory.PushTenantAdministration(selection.OperationalContext),
                IdentityConsoleContextKind.Workspace => scopeFactory.Push(selection.OperationalContext),
                _ => throw new InvalidOperationException("Unsupported Console context kind.")
            };
            await next(httpContext);
        }
        finally
        {
            workspaceScope?.Dispose();
        }
    }

    private static bool TryGetConsoleScope(
        HttpRequest request,
        out string scopeRef,
        out bool persistSelection,
        out bool clearInvalidSelection)
    {
        scopeRef = request.Headers[ConsoleContextHeader].FirstOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(scopeRef))
        {
            persistSelection = false;
            clearInvalidSelection = false;
            return true;
        }
        scopeRef = request.Query["context"].FirstOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(scopeRef))
        {
            persistSelection = true;
            clearInvalidSelection = true;
            return true;
        }
        persistSelection = false;
        clearInvalidSelection = true;
        return request.Cookies.TryGetValue(ConsoleContextCookie, out scopeRef!);
    }
}
