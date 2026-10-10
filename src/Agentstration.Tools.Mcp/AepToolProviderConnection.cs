using System.Text;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Tools.Mcp;

public sealed record ResolvedAepExtension(
    ExtensionRegistrationResource Registration,
    IAepAccessTokenProvider? AccessTokenProvider);

public interface IAepExtensionRegistrationResolver
{
    Task<ResolvedAepExtension> ResolveAsync(ToolProviderResource provider, CancellationToken cancellationToken);
}

public sealed class ResourceAepExtensionRegistrationResolver(
    IResourceStore store,
    ISecretResolver? secrets = null) : IAepExtensionRegistrationResolver
{
    public async Task<ResolvedAepExtension> ResolveAsync(
        ToolProviderResource provider,
        CancellationToken cancellationToken)
    {
        var extensionId = provider.Definition.Aep?.ExtensionId;
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ToolResolutionException("extension_invalid", "An AEP ToolProvider requires an Extension ID.");
        if (provider.ScopeRef is not { } providerScope || providerScope == default)
            throw new ToolResolutionException("extension_scope_required", "An AEP ToolProvider requires an ownership scope to resolve its extension registration.");

        const int pageSize = 200;
        var matches = new List<StoredResource<ExtensionRegistrationResource>>();
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await store.ListVisibleAsync<ExtensionRegistrationResource>(
                providerScope,
                ExtensionKinds.ExtensionRegistration,
                skip,
                pageSize,
                cancellationToken);
            matches.AddRange(page.Where(value =>
                value.Value.Definition.Enabled
                && string.Equals(value.Value.Definition.ExpectedExtensionId, extensionId, StringComparison.Ordinal)));
            if (page.Count < pageSize) break;
        }

        if (matches.Count == 0)
            throw new ToolResolutionException("extension_unavailable", $"No enabled AEP extension registration for '{extensionId}' is visible from ToolProvider '{provider.Address}'.");
        if (matches.Count > 1)
            throw new ToolResolutionException("extension_registration_ambiguous", $"More than one enabled AEP extension registration for '{extensionId}' is visible from ToolProvider '{provider.Address}'.");

        var registration = matches[0].Value;
        return new ResolvedAepExtension(registration, AepToolExtensionCredentials.Create(registration, secrets));
    }
}

internal static class AepToolExtensionCredentials
{
    public static IAepAccessTokenProvider? Create(
        ExtensionRegistrationResource registration,
        ISecretResolver? secrets)
    {
        var definition = registration.Definition;
        if (definition.AuthenticationMode == AepTransportAuthenticationMode.None)
        {
            if (definition.Credential is not null)
                throw new ToolResolutionException("credential_configuration_invalid", "An AEP credential cannot be configured when authentication mode is none.");
            return null;
        }
        if (definition.AuthenticationMode != AepTransportAuthenticationMode.StaticBearer)
            throw new ToolResolutionException("authentication_mode_unsupported", $"AEP authentication mode '{definition.AuthenticationMode}' is not supported.");
        if (definition.Credential is null || registration.ScopeRef is not { } scopeRef || scopeRef == default || secrets is null)
            throw new ToolResolutionException("credential_configuration_invalid", "Static Bearer AEP authentication requires a scoped Secret and an available secret resolver.");
        return new ToolAepAccessTokenProvider(
            secrets,
            definition.Credential,
            registration.Namespace,
            scopeRef,
            registration.Name);
    }
}

internal sealed class ToolAepAccessTokenProvider(
    ISecretResolver secrets,
    ResourceReference credential,
    ResourceNamespace extensionNamespace,
    ResourceScopeRef extensionScopeRef,
    string extensionName) : IAepAccessTokenProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var address = credential.Resolve(extensionNamespace, SecretResourceKinds.Secret);
        using var resolved = await secrets.ResolveAsync(
            new SecretReference(address, credential.ScopeRef),
            new SecretResolutionContext(
                extensionScopeRef,
                new ResourceAddress(extensionNamespace, ExtensionKinds.ExtensionRegistration, extensionName)),
            cancellationToken);
        if (resolved is null)
            throw new ToolResolutionException("credential_unavailable", $"The AEP credential Secret '{address}' is unavailable.");
        string token;
        try
        {
            token = StrictUtf8.GetString(resolved.Value.AccessValue().Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ToolResolutionException("credential_invalid", $"The AEP credential Secret '{address}' is not valid UTF-8.", exception);
        }
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
            throw new ToolResolutionException("credential_invalid", $"The AEP credential Secret '{address}' is not a valid Bearer token.");
        return token;
    }
}
