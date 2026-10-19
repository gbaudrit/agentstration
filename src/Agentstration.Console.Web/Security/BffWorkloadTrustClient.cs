using System.Net.Http.Json;

namespace Agentstration.Console.Web.Security;

public sealed record BffWorkloadTrust(string WorkloadId, string CredentialId, string InstanceId);

public interface IBffWorkloadTrustClient
{
    Task<BffWorkloadTrust> ResolveAsync(CancellationToken cancellationToken);
}

public sealed class BffWorkloadTrustClient(HttpClient client) : IBffWorkloadTrustClient
{
    public async Task<BffWorkloadTrust> ResolveAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<BffWorkloadTrust>("api/internal/bff/trust", cancellationToken)
        ?? throw new InvalidOperationException("The BFF workload trust response was empty.");
}
