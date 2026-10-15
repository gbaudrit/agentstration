using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tools;

public sealed class AepEnrollmentToolProvisioner(ToolManagementService tools) : IAepEnrollmentResourceProvisioner
{
    public async Task ProvisionAsync(
        ExtensionRegistrationResource registration,
        AepManifest? authenticatedManifest,
        CancellationToken cancellationToken)
    {
        var scopeRef = registration.ScopeRef
            ?? throw new ToolResourceValidationException("The Extension Registration has no ownership scope.");
        var extensionReference = new ResourceReference(registration.Name, scopeRef, registration.Namespace);
        var provider = new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata
            {
                Name = registration.Name,
                Namespace = registration.Namespace
            },
            ScopeRef = scopeRef,
            Generation = 1,
            Definition = new ToolProviderProperties
            {
                DisplayName = registration.Definition.DisplayName,
                ProviderType = ToolProviderType.Aep,
                Aep = new AepToolProviderConfiguration { Extension = extensionReference }
            }
        };

        var existing = await tools.GetProviderExactAsync(scopeRef, provider.Name, provider.Namespace, cancellationToken);
        if (existing is not null && !IsOwnedBy(existing.Value, extensionReference))
            throw new ToolResourceValidationException($"ToolProvider '{provider.Address}' is already bound to another connection.");

        if (existing is null && authenticatedManifest is not null
            && (authenticatedManifest.Contributions.Tools?.Count ?? 0) == 0)
            return;

        var discovery = await tools.TestConnectionAsync(provider, cancellationToken);
        if (existing is null && discovery.Tools.Count == 0)
            return;

        if (existing is null || !ConfigurationEquals(existing.Value, provider))
            _ = await tools.PutProviderAsync(provider, existing?.ETag, existing is null, cancellationToken);

        _ = await tools.RefreshDiscoveryExactAsync(scopeRef, provider.Name, provider.Namespace, cancellationToken);
    }

    private static bool IsOwnedBy(ToolProviderResource provider, ResourceReference extension) =>
        provider.Definition.ProviderType == ToolProviderType.Aep
        && provider.Definition.Aep?.Extension == extension;

    private static bool ConfigurationEquals(ToolProviderResource left, ToolProviderResource right) =>
        left.Definition.ProviderType == right.Definition.ProviderType
        && left.Definition.Enabled == right.Definition.Enabled
        && string.Equals(left.Definition.DisplayName, right.Definition.DisplayName, StringComparison.Ordinal)
        && left.Definition.Aep?.Extension == right.Definition.Aep?.Extension;
}
