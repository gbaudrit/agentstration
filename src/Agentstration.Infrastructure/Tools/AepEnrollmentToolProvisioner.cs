using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tools;

public sealed partial class AepEnrollmentToolProvisioner(
    ToolManagementService tools,
    ToolSetService toolSets,
    IResourceStore store,
    IResourceGovernanceContext governance) : IAepEnrollmentResourceProvisioner
{
    public async Task ProvisionAsync(
        ExtensionRegistrationResource registration,
        AepManifest? authenticatedManifest,
        CancellationToken cancellationToken)
    {
        if (authenticatedManifest is not null)
        {
            var errors = AepToolSetDescriptorValidator.Validate(authenticatedManifest);
            if (errors.Count > 0) throw new ToolResourceValidationException(string.Join(" ", errors));
        }
        var scopeRef = registration.ScopeRef
            ?? throw new ToolResourceValidationException("The Extension Registration has no ownership scope.");
        var extensionId = authenticatedManifest?.Extension.Id
            ?? registration.Definition.ExpectedExtensionId
            ?? registration.Name;
        var authority = ResourceGovernance.Extension(
            $"{scopeRef.Value}/{registration.Kind}/{registration.Namespace}/{registration.Name}",
            extensionId,
            authenticatedManifest?.Extension.Version);
        using var governanceScope = governance.Push(authority);
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
        var requiredTools = (authenticatedManifest?.Contributions.DataSourceProfileBundles ?? [])
            .SelectMany(value => value.RequiredTools ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (requiredTools.Count == 0 && (authenticatedManifest?.Contributions.ToolSets?.Count ?? 0) == 0) return;
        var materialized = (await store.ListExactAsync<ToolResource>(scopeRef,
                ToolResourceKinds.Tool, 0, int.MaxValue, cancellationToken))
            .Where(value => value.Value.Namespace == provider.Namespace
                && value.Value.Definition.Provider?.Name == provider.Name
                && value.Value.Definition.Provider.ScopeRef == scopeRef)
            .ToArray();
        foreach (var externalId in requiredTools)
        {
            var tool = materialized.SingleOrDefault(value =>
                string.Equals(value.Value.Definition.ExternalId, externalId, StringComparison.Ordinal))
                ?? throw new ToolResourceValidationException(
                    $"Required bundle Tool contribution '{externalId}' was not materialized by registration '{registration.Name}'.");
            if (tool.Value.Definition.Enabled) continue;
            _ = await store.PutExactAsync(scopeRef, tool.Value with
            {
                Generation = checked(tool.Value.Generation + 1),
                Definition = tool.Value.Definition with { Enabled = true }
            }, tool.ETag, false, cancellationToken);
        }
        await ProvisionToolSetsAsync(registration, authenticatedManifest, scopeRef, authority, materialized, cancellationToken);
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
