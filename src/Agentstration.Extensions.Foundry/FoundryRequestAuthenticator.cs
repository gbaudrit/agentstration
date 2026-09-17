using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.Foundry;

public sealed class FoundryRequestAuthenticator
{
    private static readonly TokenRequestContext ProjectTokenContext = new(["https://ai.azure.com/.default"]);
    private static readonly TokenRequestContext ResourceInferenceTokenContext = new(["https://cognitiveservices.azure.com/.default"]);
    private readonly TokenCredential? credential;
    private readonly string? apiKey;

    public FoundryRequestAuthenticator(FoundryExtensionOptions options, string? developmentApiKey = null, TokenCredential? tokenCredential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AuthenticationMode == FoundryAuthenticationMode.ApiKeyEnvironment)
        {
            apiKey = Environment.GetEnvironmentVariable("FOUNDRY_API_KEY") ?? developmentApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(char.IsWhiteSpace) || apiKey.Any(char.IsControl))
                throw new InvalidOperationException("FOUNDRY_API_KEY must contain a bounded, single-line API key in ApiKeyEnvironment mode.");
            return;
        }
        credential = tokenCredential ?? (options.AuthenticationMode switch
        {
            FoundryAuthenticationMode.ManagedIdentity => new ManagedIdentityCredential(
                options.ManagedIdentityClientId is { Length: > 0 } clientId
                    ? ManagedIdentityId.FromUserAssignedClientId(clientId)
                    : ManagedIdentityId.SystemAssigned),
            FoundryAuthenticationMode.WorkloadIdentity => CreateWorkloadIdentity(),
            FoundryAuthenticationMode.Development => new AzureCliCredential(),
            _ => throw new InvalidOperationException("Unsupported Foundry authentication mode.")
        });
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        ApplyAsync(request, ProjectTokenContext, cancellationToken);

    public Task ApplyInferenceAsync(HttpRequestMessage request, FoundryExtensionOptions options, CancellationToken cancellationToken) =>
        ApplyAsync(
            request,
            options.InferenceEndpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal)
                ? ProjectTokenContext : ResourceInferenceTokenContext,
            cancellationToken);

    private async Task ApplyAsync(HttpRequestMessage request, TokenRequestContext tokenContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (apiKey is not null)
        {
            request.Headers.Add("api-key", apiKey);
            return;
        }
        try
        {
            var token = await credential!.GetTokenAsync(tokenContext, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AuthenticationFailedException)
        {
            throw new AepServerException("authentication_failed", "Foundry identity authentication failed.", 502);
        }
    }

    private static WorkloadIdentityCredential CreateWorkloadIdentity()
    {
        var tokenFile = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_TENANT_ID"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"))
            || string.IsNullOrWhiteSpace(tokenFile) || !File.Exists(tokenFile))
            throw new InvalidOperationException("WorkloadIdentity mode requires AZURE_TENANT_ID, AZURE_CLIENT_ID, and AZURE_FEDERATED_TOKEN_FILE.");
        return new WorkloadIdentityCredential();
    }
}
