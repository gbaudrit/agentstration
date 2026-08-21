using System.Net;
using Agentstration.Management.Abstractions;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agentstration.Web.Security;

public sealed class ConsoleRealtimeSession(
    IHttpContextAccessor httpContextAccessor,
    ICurrentRequestContext requestContext)
{
    public void Configure(Uri endpoint, HttpConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        var incoming = httpContextAccessor.HttpContext;
        if (incoming?.User.Identity?.IsAuthenticated != true
            || !requestContext.IsInitialized
            || !IsSameOrigin(incoming.Request, endpoint)) return;

        var cookies = new CookieContainer();
        foreach (var cookie in SessionCookies(incoming.Request.Cookies))
            cookies.Add(endpoint, new Cookie(cookie.Key, cookie.Value) { Path = "/" });

        options.Cookies = cookies;
        options.Headers[PrincipalResolutionMiddleware.WorkspaceHeader] = requestContext.Current.WorkspaceId.ToString("D");
    }

    private static IEnumerable<KeyValuePair<string, string>> SessionCookies(IRequestCookieCollection cookies)
    {
        if (!cookies.TryGetValue(AgentstrationAuthenticationDefaults.ApplicationCookie, out var sessionCookie)
            || string.IsNullOrWhiteSpace(sessionCookie)) yield break;

        yield return new(AgentstrationAuthenticationDefaults.ApplicationCookie, sessionCookie);
        if (!sessionCookie.StartsWith("chunks-", StringComparison.Ordinal)) yield break;

        foreach (var cookie in cookies
                     .Where(cookie => cookie.Key.StartsWith($"{AgentstrationAuthenticationDefaults.ApplicationCookie}C", StringComparison.Ordinal))
                     .OrderBy(cookie => cookie.Key, StringComparer.Ordinal))
            yield return cookie;
    }

    private static bool IsSameOrigin(HttpRequest request, Uri endpoint)
    {
        var requestPort = request.Host.Port ?? DefaultPort(request.Scheme);
        var endpointPort = endpoint.IsDefaultPort ? DefaultPort(endpoint.Scheme) : endpoint.Port;
        return string.Equals(request.Scheme, endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Host.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
            && requestPort == endpointPort;
    }

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}
