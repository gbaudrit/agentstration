using Microsoft.Net.Http.Headers;

namespace Agentstration.Workplace.Web;

public sealed class WorkplaceApiSessionHandler(
    IHttpContextAccessor httpContextAccessor,
    Uri trustedBaseAddress,
    string sessionCookieName,
    string workspaceCookieName) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var incoming = httpContextAccessor.HttpContext;
        if (incoming is not null
            && IsTrusted(request.RequestUri)
            && !request.Headers.Contains(HeaderNames.Cookie)
            && incoming.Request.Cookies.TryGetValue(sessionCookieName, out var sessionCookie)
            && !string.IsNullOrWhiteSpace(sessionCookie))
        {
            var cookies = new List<string> { $"{sessionCookieName}={sessionCookie}" };
            if (sessionCookie.StartsWith("chunks-", StringComparison.Ordinal))
                cookies.AddRange(incoming.Request.Cookies
                    .Where(cookie => cookie.Key.StartsWith($"{sessionCookieName}C", StringComparison.Ordinal))
                    .OrderBy(cookie => cookie.Key, StringComparer.Ordinal)
                    .Select(cookie => $"{cookie.Key}={cookie.Value}"));
            if (incoming.Request.Cookies.TryGetValue(workspaceCookieName, out var workspaceCookie)
                && !string.IsNullOrWhiteSpace(workspaceCookie))
                cookies.Add($"{workspaceCookieName}={workspaceCookie}");
            request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, string.Join("; ", cookies));
        }

        return base.SendAsync(request, cancellationToken);
    }

    private bool IsTrusted(Uri? requestUri) =>
        requestUri is { IsAbsoluteUri: true }
        && string.Equals(requestUri.Scheme, trustedBaseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(requestUri.Host, trustedBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && requestUri.Port == trustedBaseAddress.Port;
}
