using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;

namespace Agentstration.Extensions.Foundry;

public sealed class FoundryRequestAuthenticator
{
    private static readonly TokenRequestContext ProjectTokenContext = new(["https://ai.azure.com/.default"]);
    private static readonly TokenRequestContext ResourceInferenceTokenContext = new(["https://cognitiveservices.azure.com/.default"]);
    private readonly TokenCredential? credential;
    private readonly string? apiKey;
    private readonly FoundryExtensionOptions options;
    private readonly IHttpClientFactory? httpClientFactory;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public FoundryRequestAuthenticator(FoundryExtensionOptions options, string? developmentApiKey = null,
        TokenCredential? tokenCredential = null, IHttpClientFactory? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.httpClientFactory = httpClientFactory;
        if (options.AuthenticationMode == FoundryAuthenticationMode.ApiKeyBinding) return;
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

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ValidateTarget(request, options.DeploymentsEndpoint(), HttpMethod.Get, discovery: true);
        return ApplyAsync(request, ProjectTokenContext, cancellationToken);
    }

    public Task ApplyInferenceAsync(HttpRequestMessage request, FoundryExtensionOptions options,
        AepChatRequest chat, CancellationToken cancellationToken, string? boundKey = null)
    {
        if (options.ProjectEndpoint != this.options.ProjectEndpoint
            || options.InferenceEndpoint != this.options.InferenceEndpoint
            || options.AuthenticationMode != this.options.AuthenticationMode)
            throw new AepServerException("provider_target_invalid", "Foundry authentication requires the configured endpoint.");
        ValidateTarget(request, new Uri(options.InferenceEndpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions"), HttpMethod.Post, discovery: false);
        if (boundKey is not null)
        {
            request.Headers.Add("api-key", boundKey);
            return Task.CompletedTask;
        }
        if (chat.SecretAccess is { Count: > 0 })
            return ApplyBoundKeyAsync(request, chat.SecretAccess, cancellationToken);
        return ApplyAsync(request,
            options.InferenceEndpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal)
                ? ProjectTokenContext : ResourceInferenceTokenContext,
            cancellationToken);
    }

    public void ApplyBoundDiscovery(HttpRequestMessage request, string key)
    {
        ValidateTarget(request, options.DeploymentsEndpoint(), HttpMethod.Get, discovery: true);
        request.Headers.Add("api-key", key);
    }

    public async Task<string> RedeemBoundKeyAsync(IReadOnlyList<AepSecretAccessGrant> grants,
        CancellationToken cancellationToken)
    {
        if (grants.Count != 1 || grants[0].RequirementId != "credential" || httpClientFactory is null)
            throw new AepServerException("secret_binding_invalid", "Foundry requires one bound credential grant.", 400);
        using var client = httpClientFactory.CreateClient("foundry-secret-access");
        byte[] value;
        try { value = await new AepSecretAccessClient(client).RedeemAsync(grants[0], cancellationToken); }
        catch (AepProtocolException exception)
        {
            throw new AepServerException(exception.Code, "Foundry credential access failed.", 502);
        }
        try
        {
            var key = StrictUtf8.GetString(value);
            if (string.IsNullOrWhiteSpace(key) || key.Length > 8192 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
                throw new AepServerException("secret_value_invalid", "Foundry credential has invalid encoding.", 502);
            return key;
        }
        catch (DecoderFallbackException)
        {
            throw new AepServerException("secret_value_invalid", "Foundry credential has invalid encoding.", 502);
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    public Task ApplyInferenceAsync(HttpRequestMessage request, FoundryExtensionOptions options,
        CancellationToken cancellationToken) =>
        ApplyInferenceAsync(request, options, new AepChatRequest(string.Empty, []), cancellationToken);

    private async Task ApplyBoundKeyAsync(HttpRequestMessage request,
        IReadOnlyList<AepSecretAccessGrant> grants, CancellationToken cancellationToken)
    {
        request.Headers.Add("api-key", await RedeemBoundKeyAsync(grants, cancellationToken));
    }

    private static void ValidateTarget(HttpRequestMessage request, Uri expected, HttpMethod method, bool discovery)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.RequestUri;
        if (request.Method != method || target is null || !target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps
            || !string.Equals(target.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            || target.Port != expected.Port || target.AbsolutePath != expected.AbsolutePath
            || target.UserInfo.Length > 0 || target.Fragment.Length > 0
            || (discovery ? target.Query.Length > 2048 : target.Query.Length > 0))
            throw new AepServerException("provider_target_invalid", "Foundry authentication requires the configured endpoint.");
    }

    private async Task ApplyAsync(HttpRequestMessage request, TokenRequestContext tokenContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (apiKey is not null)
        {
            request.Headers.Add("api-key", apiKey);
            return;
        }
        if (options.AuthenticationMode == FoundryAuthenticationMode.ApiKeyBinding)
            throw new AepServerException("secret_binding_required", "Foundry requires a bound credential.", 400);
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
