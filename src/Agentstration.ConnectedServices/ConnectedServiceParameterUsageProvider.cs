using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.ConnectedServices;

public sealed class ConnectedServiceParameterUsageProvider(
    IResourceStore store,
    IRequestContextScopeFactory requestContexts) : IParameterUsageProvider
{
    public async Task<IReadOnlyList<ParameterUsage>> GetUsagesAsync(
        ScopedResourceAddress parameter,
        CancellationToken cancellationToken = default)
    {
        using var system = requestContexts.PushSystem();
        return (await store.ListAllAsync<ConnectedServiceResource>(
                ConnectedServiceResourceKinds.ConnectedService, cancellationToken))
            .Where(service => service.Value.Definition.Bindings.Any(binding =>
                binding.Kind == ConnectedServiceBindingKind.Parameter
                && binding.Parameter is { } reference
                && reference.ScopeRef == parameter.ScopeRef
                && reference.Address == parameter.Address))
            .Select(service => new ParameterUsage(
                service.Value.Kind,
                service.Value.Name,
                service.Value.Definition.DisplayName,
                $"/connected-services/{Uri.EscapeDataString(service.Value.Name)}"))
            .ToArray();
    }
}
