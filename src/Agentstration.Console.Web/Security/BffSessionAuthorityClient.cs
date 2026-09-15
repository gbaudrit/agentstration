using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;

namespace Agentstration.Console.Web.Security;

public enum BffLocalAuthenticationOutcome
{
    Succeeded,
    Failed,
    LockedOut,
    Unavailable
}

public sealed record BffLocalAuthenticationResult(
    BffLocalAuthenticationOutcome Outcome,
    BffSessionIdentityResponse? Identity = null);

public interface IBffSessionAuthorityClient
{
    Task<BffLocalAuthenticationResult> AuthenticateLocalAsync(
        string userName,
        string password,
        CancellationToken cancellationToken);

    Task<BffSessionValidationResponse> ValidateAsync(
        BffSessionValidationRequest request,
        CancellationToken cancellationToken);
}

public sealed class BffSessionAuthorityClient(HttpClient client) : IBffSessionAuthorityClient
{
    public async Task<BffLocalAuthenticationResult> AuthenticateLocalAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "api/internal/bff/sessions/local",
            new BffLocalSessionRequest(userName, password),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new(BffLocalAuthenticationOutcome.Failed);
        if ((int)response.StatusCode == StatusCodes.Status423Locked)
            return new(BffLocalAuthenticationOutcome.LockedOut);
        if (!response.IsSuccessStatusCode)
            return new(BffLocalAuthenticationOutcome.Unavailable);
        var identity = await response.Content.ReadFromJsonAsync<BffSessionIdentityResponse>(cancellationToken);
        return identity is null
            ? new(BffLocalAuthenticationOutcome.Unavailable)
            : new(BffLocalAuthenticationOutcome.Succeeded, identity);
    }

    public async Task<BffSessionValidationResponse> ValidateAsync(
        BffSessionValidationRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "api/internal/bff/sessions/validate",
            request,
            cancellationToken);
        if (!response.IsSuccessStatusCode) return new(false, null, "authority-unavailable");
        return await response.Content.ReadFromJsonAsync<BffSessionValidationResponse>(cancellationToken)
            ?? new(false, null, "authority-response-empty");
    }
}
