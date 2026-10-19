using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Models;

public sealed class ModelProviderParameterUsageProvider(
    IResourceStore store,
    IRequestContextScopeFactory requestContexts) : IParameterUsageProvider
{
    public async Task<IReadOnlyList<ParameterUsage>> GetUsagesAsync(
        ScopedResourceAddress parameter,
        CancellationToken cancellationToken = default)
    {
        using var system = requestContexts.PushSystem();
        return (await store.ListAllAsync<ModelProviderResource>(ModelResourceKinds.ModelProvider, cancellationToken))
            .Where(provider => provider.Value.Definition.ValueBindings.Any(binding =>
                binding.Kind == ModelProviderValueBindingKind.Parameter
                && binding.Parameter is { } reference
                && reference.ScopeRef == parameter.ScopeRef
                && reference.Address == parameter.Address))
            .Select(provider => new ParameterUsage(
                provider.Value.Kind,
                provider.Value.Name,
                provider.Value.Definition.DisplayName,
                $"/modelproviders/{Uri.EscapeDataString(provider.Value.Name)}"))
            .ToArray();
    }
}
