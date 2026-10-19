using System.Net.Http.Headers;
using Agentstration.Aep.AspNetCore;
using Azure.Core;
using Azure.Identity;

namespace Agentstration.Extensions.Foundry;

public sealed class FoundryRequestAuthenticator
{
    private static readonly TokenRequestContext ProjectTokenContext = new(["https://ai.azure.com/.default"]);
    private static readonly TokenRequestContext ResourceInferenceTokenContext = new(["https://cognitiveservices.azure.com/.default"]);
    private readonly FoundryConnection connection;
    private readonly TokenCredential? tokenCredential;

    public FoundryRequestAuthenticator(FoundryConnection connection, TokenCredential? tokenCredential = null)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        connection.Validate();
        this.tokenCredential = tokenCredential ?? CreateCredential(connection);
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ValidateTarget(request, connection.DeploymentsEndpoint(), HttpMethod.Get, true);
        return ApplyAsync(request, ProjectTokenContext, cancellationToken);
    }
    public Task ApplyInferenceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ValidateTarget(request, new Uri(connection.InferenceEndpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions"), HttpMethod.Post, false);
        return ApplyAsync(request, connection.InferenceEndpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal)
            ? ProjectTokenContext : ResourceInferenceTokenContext, cancellationToken);
    }

    private static TokenCredential? CreateCredential(FoundryConnection value) => value.AuthenticationMode switch
    {
        FoundryAuthenticationMode.ApiKey => null,
        FoundryAuthenticationMode.ManagedIdentity => new ManagedIdentityCredential(value.ManagedIdentityClientId is { Length: > 0 } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned),
        FoundryAuthenticationMode.WorkloadIdentity => new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
        { TenantId = value.WorkloadIdentityTenantId, ClientId = value.WorkloadIdentityClientId, TokenFilePath = value.WorkloadIdentityTokenFile }),
        FoundryAuthenticationMode.Development => new AzureCliCredential(),
        _ => throw new InvalidOperationException("Unsupported Foundry authentication mode.")
    };

    private static void ValidateTarget(HttpRequestMessage request, Uri expected, HttpMethod method, bool discovery)
    {
        var target = request.RequestUri;
        if (request.Method != method || target is null || !target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps
            || !string.Equals(target.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) || target.Port != expected.Port
            || target.AbsolutePath != expected.AbsolutePath || target.UserInfo.Length > 0 || target.Fragment.Length > 0
            || (discovery ? target.Query.Length > 2048 : target.Query.Length > 0))
            throw new AepServerException("provider_target_invalid", "Foundry authentication requires the resolved endpoint.");
    }

    private async Task ApplyAsync(HttpRequestMessage request, TokenRequestContext tokenContext, CancellationToken cancellationToken)
    {
        if (connection.AuthenticationMode == FoundryAuthenticationMode.ApiKey) { request.Headers.Add("api-key", connection.Credential!); return; }
        try
        {
            var token = await tokenCredential!.GetTokenAsync(tokenContext, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AuthenticationFailedException) { throw new AepServerException("authentication_failed", "Foundry identity authentication failed.", 502); }
    }
}
