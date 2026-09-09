using System.Text;
using Agentstration.Aep.Client;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.ModelProviders;

internal sealed class SecretAepAccessTokenProvider(
    ISecretResolver secrets,
    ResourceReference credential,
    ResourceNamespace extensionNamespace,
    ResourceScopeRef extensionScopeRef,
    string extensionName) : IAepAccessTokenProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var address = credential.Resolve(extensionNamespace, ResourceKinds.Secret);
        using var resolved = await secrets.ResolveAsync(
            new SecretReference(address, credential.ScopeRef),
            new SecretResolutionContext(
                extensionScopeRef,
                new ResourceAddress(extensionNamespace, ResourceKinds.ExtensionRegistration, extensionName)),
            cancellationToken);
        if (resolved is null)
            throw new AepProtocolException("credential_unavailable", $"The AEP credential Secret '{address}' is unavailable.");
        string token;
        try
        {
            token = StrictUtf8.GetString(resolved.Value.AccessValue().Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AepProtocolException("credential_invalid", $"The AEP credential Secret '{address}' is not valid UTF-8.", innerException: exception);
        }
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
            throw new AepProtocolException("credential_invalid", $"The AEP credential Secret '{address}' is not a valid Bearer token.");
        return token;
    }
}

internal static class AepExtensionCredentials
{
    public static IAepAccessTokenProvider? Create(
        AepTransportAuthenticationMode authenticationMode,
        ResourceReference? credential,
        ResourceNamespace extensionNamespace,
        ResourceScopeRef? extensionScopeRef,
        string? extensionName,
        ISecretResolver? secrets)
    {
        if (authenticationMode == AepTransportAuthenticationMode.None)
        {
            if (credential is not null)
                throw new AepProtocolException("credential_configuration_invalid", "An AEP credential cannot be configured when authentication mode is none.");
            return null;
        }
        if (authenticationMode != AepTransportAuthenticationMode.StaticBearer)
            throw new AepProtocolException("authentication_mode_unsupported", $"AEP authentication mode '{authenticationMode}' is not supported.");
        if (credential is null || extensionScopeRef is null || string.IsNullOrWhiteSpace(extensionName) || secrets is null)
            throw new AepProtocolException("credential_configuration_invalid", "Static Bearer AEP authentication requires a scoped Secret and an available secret resolver.");
        return new SecretAepAccessTokenProvider(secrets, credential, extensionNamespace, extensionScopeRef.Value, extensionName);
    }
}
