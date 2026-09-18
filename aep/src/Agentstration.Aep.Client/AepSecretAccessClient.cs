using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Client;

/// <summary>Redeems a host-issued, one-use Secret grant during an AEP operation.</summary>
public sealed class AepSecretAccessClient(HttpClient httpClient)
{
    private static readonly HashSet<string> KnownErrors = new(StringComparer.Ordinal)
    {
        "secret_access_version_unsupported", "capability_invalid", "capability_expired",
        "context_mismatch", "context_terminated", "access_denied", "secret_unavailable",
        "vault_unavailable", "secret_value_invalid", "rate_limited"
    };

    public async Task<byte[]> RedeemAsync(AepSecretAccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(grant.Version, AepProtocol.SecretAccessVersion, StringComparison.Ordinal))
            throw new AepProtocolException("secret_access_version_unsupported", "The Secret access protocol version is unsupported.");
        if (grant.Endpoint.Scheme != Uri.UriSchemeHttps
            && !(grant.Endpoint.Scheme == Uri.UriSchemeHttp && grant.Endpoint.IsLoopback))
            throw new AepProtocolException("secret_access_endpoint_invalid", "The Secret access endpoint must use HTTPS or loopback HTTP.");

        using var request = new HttpRequestMessage(HttpMethod.Post, grant.Endpoint)
        {
            Content = JsonContent.Create(new AepSecretAccessRequest(
                grant.Version, grant.ExtensionId, grant.RequirementId, grant.ExecutionId, grant.SecretCapability),
                options: AepProtocol.JsonOptions)
        };
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength is > 100_000)
            throw new AepProtocolException("secret_access_response_too_large", "The Secret access response exceeds the protocol limit.", response.StatusCode);
        try { await response.Content.LoadIntoBufferAsync(100_000, cancellationToken); }
        catch (HttpRequestException exception)
        {
            throw new AepProtocolException("secret_access_response_too_large", "The Secret access response exceeds the protocol limit.", response.StatusCode, exception);
        }
        if (!response.IsSuccessStatusCode)
        {
            AepErrorResponse? error;
            try { error = await response.Content.ReadFromJsonAsync<AepErrorResponse>(AepProtocol.JsonOptions, cancellationToken); }
            catch (JsonException) { error = null; }
            var code = error?.Error.Code;
            throw new AepProtocolException(code is not null && KnownErrors.Contains(code) ? code : "secret_access_failed",
                "The Secret access request failed.", response.StatusCode);
        }
        AepSecretAccessResponse? payload;
        try { payload = await response.Content.ReadFromJsonAsync<AepSecretAccessResponse>(AepProtocol.JsonOptions, cancellationToken); }
        catch (JsonException exception)
        {
            throw new AepProtocolException("secret_value_invalid", "The Secret access response is invalid.", response.StatusCode, exception);
        }
        if (payload is null || payload.Version != AepProtocol.SecretAccessVersion
            || string.IsNullOrWhiteSpace(payload.SecretValueBase64))
            throw new AepProtocolException("secret_value_invalid", "The Secret access response is invalid.", response.StatusCode);
        try
        {
            var value = Convert.FromBase64String(payload.SecretValueBase64);
            if (value.Length is < 1 or > 65_536)
            {
                CryptographicOperations.ZeroMemory(value);
                throw new AepProtocolException("secret_value_invalid", "The Secret access value exceeds the protocol limit.", response.StatusCode);
            }
            return value;
        }
        catch (FormatException exception)
        {
            throw new AepProtocolException("secret_value_invalid", "The Secret access value has invalid encoding.", response.StatusCode, exception);
        }
    }
}
