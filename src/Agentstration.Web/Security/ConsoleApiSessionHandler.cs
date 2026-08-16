using Agentstration.Management.Abstractions;
using Agentstration.Web.Hosting;
using Microsoft.Net.Http.Headers;

namespace Agentstration.Web.Security;

public sealed class ConsoleApiSessionHandler(
    IHttpContextAccessor httpContextAccessor,
    ICurrentRequestContext requestContext,
    Uri trustedBaseAddress,
    string sessionCookieName) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var incoming = httpContextAccessor.HttpContext;
        if (incoming?.User.Identity?.IsAuthenticated == true
            && IsTrusted(request.RequestUri)
            && !request.Headers.Contains(HeaderNames.Cookie)
            && incoming.Request.Cookies.TryGetValue(sessionCookieName, out var sessionCookie)
            && !string.IsNullOrWhiteSpace(sessionCookie))
        {
            var cookieValues = new List<string> { $"{sessionCookieName}={sessionCookie}" };
            if (sessionCookie.StartsWith("chunks-", StringComparison.Ordinal))
                cookieValues.AddRange(incoming.Request.Cookies
                    .Where(cookie => cookie.Key.StartsWith($"{sessionCookieName}C", StringComparison.Ordinal))
                    .OrderBy(cookie => cookie.Key, StringComparer.Ordinal)
                    .Select(cookie => $"{cookie.Key}={cookie.Value}"));
            request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, string.Join("; ", cookieValues));
            if (requestContext.IsInitialized && !request.Headers.Contains(PrincipalResolutionMiddleware.WorkspaceHeader))
                request.Headers.TryAddWithoutValidation(
                    PrincipalResolutionMiddleware.WorkspaceHeader,
                    requestContext.Current.WorkspaceId.ToString("D"));
        }

        return base.SendAsync(request, cancellationToken);
    }

    private bool IsTrusted(Uri? requestUri) =>
        requestUri is { IsAbsoluteUri: true }
        && string.Equals(requestUri.Scheme, trustedBaseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requestUri.Host, trustedBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && requestUri.Port == trustedBaseAddress.Port;
}
