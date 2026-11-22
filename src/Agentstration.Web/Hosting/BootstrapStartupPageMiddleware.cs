using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentstration.Infrastructure;
using Agentstration.Web.Pages;
using Agentstration.Web.Security;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Hosting;

// Runs before authentication: rendering this page must not read identity or instance data.
internal sealed class BootstrapStartupPageMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, BootstrapStartupState startup, IStringLocalizer<AuthStrings> strings)
    {
        var request = context.Request;
        var loginAvailable = startup.IsReady || startup.AdministrationReady;
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && request.Path == "/startup/ready")
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = loginAvailable ? StatusCodes.Status204NoContent : StatusCodes.Status503ServiceUnavailable;
            if (!loginAvailable) context.Response.Headers.RetryAfter = "2";
            return;
        }

        var startupPage = request.Path == "/startup";
        var browserNavigation = HttpMethods.IsGet(request.Method)
            && !request.Path.StartsWithSegments("/api")
            && !request.Path.StartsWithSegments("/health")
            && request.GetTypedHeaders().Accept?.Any(value =>
                value.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) && value.Quality is null or > 0) == true;
        if (!HttpMethods.IsGet(request.Method) || (!startupPage && (loginAvailable || !browserNavigation)))
        {
            await next(context);
            return;
        }

        var pathBase = AuthenticationReturnUrls.Normalize(request.PathBase.Value).TrimEnd('/');
        var returnUrl = startupPage || request.Path == "/login"
            ? AuthenticationReturnUrls.Normalize(request.Query["ReturnUrl"])
            : AuthenticationReturnUrls.Normalize(request.PathBase + request.Path + request.QueryString);
        var loginUrl = $"{pathBase}/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        context.Response.Headers.CacheControl = "no-store";
        if (loginAvailable)
        {
            await Results.LocalRedirect(loginUrl).ExecuteAsync(context);
            return;
        }

        var title = HtmlEncoder.Default.Encode(strings["InstanceStarting"].Value);
        var culture = HtmlEncoder.Default.Encode(CultureInfo.CurrentUICulture.Name);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($$"""
            <!DOCTYPE html>
            <html lang="{{culture}}">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{title}}</title>
                <noscript><meta http-equiv="refresh" content="3"></noscript>
                <style>
                    :root { color-scheme: light; font-family: system-ui, sans-serif; color: #111827; background: #f4f7fc; }
                    body { margin: 0; }
                    main { min-height: 100svh; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 24px; padding: 24px; box-sizing: border-box; text-align: center; }
                    h1 { margin: 0; font-size: clamp(1.25rem, 4vw, 1.625rem); font-weight: 500; }
                    .spinner { width: 40px; height: 40px; border: 3px solid #c9d9ff; border-top-color: #2563eb; border-radius: 50%; animation: spin 1s linear infinite; }
                    @keyframes spin { to { transform: rotate(360deg); } }
                    @media (prefers-reduced-motion: reduce) { .spinner { animation: none; } }
                </style>
            </head>
            <body>
                <main role="status" aria-live="polite">
                    <span class="spinner" aria-hidden="true"></span>
                    <h1>{{title}}</h1>
                </main>
                <script>
                    const readyUrl = {{JsonSerializer.Serialize($"{pathBase}/startup/ready")}};
                    const loginUrl = {{JsonSerializer.Serialize(loginUrl)}};
                    async function checkLogin() {
                        try {
                            const response = await fetch(readyUrl, { cache: 'no-store', credentials: 'omit', redirect: 'error', signal: AbortSignal.timeout(5000) });
                            if (response.status === 204) {
                                window.location.replace(loginUrl);
                                return;
                            }
                        } catch { /* Keep waiting through temporary network interruptions. */ }
                        window.setTimeout(checkLogin, 2000);
                    }
                    checkLogin();
                </script>
            </body>
            </html>
            """, context.RequestAborted);
    }
}
