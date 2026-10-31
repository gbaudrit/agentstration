using System.Net;
using System.Net.Http.Headers;

namespace Agentstration.Console.Web.Security;

public sealed class BffDelegationHandler(
    BffDelegationTokenProvider tokens,
    string audience,
    Uri expectedOrigin) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } destination
            || Uri.Compare(destination, expectedOrigin, UriComponents.SchemeAndServer,
                UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        var token = await tokens.GetAsync(audience, cancellationToken);
        if (token is null) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
