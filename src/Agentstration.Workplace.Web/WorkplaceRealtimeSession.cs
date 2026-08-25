using System.Net;
using Agentstration.Workplace.Client;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agentstration.Workplace.Web;

public sealed class WorkplaceRealtimeSession(
    IHttpContextAccessor httpContextAccessor,
    Uri trustedHubOrigin,
    string sessionCookieName,
    string workspaceCookieName) : IWorkplaceRealtimeConnectionOptionsConfigurator
{
    public void Configure(Uri endpoint, HttpConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        var incoming = httpContextAccessor.HttpContext;
        if (incoming is null || !IsTrusted(endpoint)) return;
        if (!incoming.Request.Cookies.TryGetValue(sessionCookieName, out var sessionCookie)
            || string.IsNullOrWhiteSpace(sessionCookie)) return;

        var cookies = new CookieContainer();
        cookies.Add(endpoint, new Cookie(sessionCookieName, sessionCookie) { Path = "/" });
        if (sessionCookie.StartsWith("chunks-", StringComparison.Ordinal))
        {
            foreach (var cookie in incoming.Request.Cookies
                         .Where(cookie => cookie.Key.StartsWith($"{sessionCookieName}C", StringComparison.Ordinal))
                         .OrderBy(cookie => cookie.Key, StringComparer.Ordinal))
                cookies.Add(endpoint, new Cookie(cookie.Key, cookie.Value) { Path = "/" });
        }

        if (incoming.Request.Cookies.TryGetValue(workspaceCookieName, out var workspaceCookie)
            && !string.IsNullOrWhiteSpace(workspaceCookie))
            cookies.Add(endpoint, new Cookie(workspaceCookieName, workspaceCookie) { Path = "/" });

        options.Cookies = cookies;
    }

    private bool IsTrusted(Uri endpoint) =>
        string.Equals(endpoint.Scheme, trustedHubOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(endpoint.Host, trustedHubOrigin.Host, StringComparison.OrdinalIgnoreCase)
        && endpoint.Port == trustedHubOrigin.Port;
}
