using System.Runtime.CompilerServices;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Models;

public sealed class ModelSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities,
    IResourceReferenceResolver references) : ISystemGraphContributor
{
    public string Family => "models-and-runtime-profiles";
    public SystemGraphContributorCoverage Coverage => new(Family,
        ["model-runtime-profile-definitions", "model-profile-provider-bindings", "model-provider-extension-bindings"], ["provider-execution-availability"]);

    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ModelProfileResource>(resources, scope, ModelResourceKinds.ModelProfile, cancellationToken))
        {
            var profile = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(profile) with { DisplayName = profile.Definition.DisplayName }, identities);
            var provider = await ResolveAsync<ModelProviderResource>(profile.Definition.Provider, profile.Namespace, ModelResourceKinds.ModelProvider, scope, cancellationToken);
            if (provider is not null)
                await builder.PinAsync(provider.Value.Uid, "definition.provider", cancellationToken, kind: "declared-reference", resolution: profile.Definition.Provider.ScopeRef is null ? "owner-relative" : "exact-owner");
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ModelProviderResource>(resources, scope, ModelResourceKinds.ModelProvider, cancellationToken))
        {
            var provider = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(provider) with { DisplayName = provider.Definition.DisplayName }, identities);
            var extension = await ResolveAsync<ExtensionRegistrationResource>(provider.Definition.Extension, provider.Namespace, ExtensionKinds.ExtensionRegistration, scope, cancellationToken);
            if (extension is not null)
                await builder.PinAsync(extension.Value.Uid, "definition.extension", cancellationToken, kind: "declared-reference", resolution: provider.Definition.Extension.ScopeRef is null ? "owner-relative" : "exact-owner");
            var contribution = builder.Build();
            // The Models family supplies only metadata for the registration it explicitly references.
            // Execution availability, endpoint/credential values and provider discovery are not read.
            yield return extension is null ? contribution : contribution with
            {
                Nodes = [.. contribution.Nodes, ResourceSystemGraph.Node(extension.Value) with { DisplayName = extension.Value.Definition.DisplayName }]
            };
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<RuntimeProfileResource>(resources, scope, RuntimeProfileResourceKinds.RuntimeProfile, cancellationToken))
            yield return new([ResourceSystemGraph.Node(stored.Value) with { DisplayName = stored.Value.Definition.DisplayName }], []);
    }

    private async Task<StoredResource<T>?> ResolveAsync<T>(ResourceReference reference, ResourceNamespace ownerNamespace,
        string kind, ResourceScopeRef owner, CancellationToken ct) where T : Resource
    {
        try { return await references.ResolveAsync<T>(reference, ownerNamespace, kind, owner, ct); }
        catch (ResourceReferenceAmbiguousException) { return null; }
        catch (ResourceReferenceOutsideScopeException) { return null; }
    }
}
