using System.Net.Http.Json;
using Agentstration.Identity.Contracts;

namespace Agentstration.Console.Web.Security;

public interface IBffDelegationClient
{
    Task<BffDelegationResponse?> IssueAsync(BffDelegationRequest request, CancellationToken cancellationToken);
}

public sealed class BffDelegationClient(HttpClient client) : IBffDelegationClient
{
    public async Task<BffDelegationResponse?> IssueAsync(BffDelegationRequest request, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "api/internal/bff/delegations", request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<BffDelegationResponse>(cancellationToken);
    }
}
