using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;

namespace Agentstration.Extensions.Aep;

public sealed class ExtensionConfigurationUsages(IResourceStore store, IRequestContextScopeFactory scopes)
    : IParameterUsageProvider, ISecretUsageProvider
{
    async Task<IReadOnlyList<ParameterUsage>> IParameterUsageProvider.GetUsagesAsync(ScopedResourceAddress parameter, CancellationToken token)
    {
        using var owner = scopes.PushSystem();
        var resources = await store.ListExactAsync<ExtensionConfigurationResource>(ResourceScopeRef.Instance,
            ExtensionKinds.ExtensionConfiguration, 0, 1001, token);
        if (resources.Count > 1000) throw new ExtensionConfigurationException("configuration_inventory_limit");
        var usages = new List<ParameterUsage>();
        foreach (var record in resources.Where(r => r.Value.Definition.Bindings.Any(b => b.Parameter is { } p && p.ScopeRef == parameter.ScopeRef && p.Address == parameter.Address)))
        {
            var registration = await store.GetByUidAsync<ExtensionRegistrationResource>(record.Value.Definition.RegistrationUid, token);
            var url = registration is null ? "/extensions" : $"/extensions/{Uri.EscapeDataString(registration.Value.Name)}?namespace={Uri.EscapeDataString(registration.Value.Namespace.Value)}&context=%2Finstance#installation";
            usages.Add(new(record.Value.Kind, record.Value.Name, registration?.Value.Definition.DisplayName ?? record.Value.Name, url));
        }
        return usages;
    }

    async Task<IReadOnlyList<SecretUsage>> ISecretUsageProvider.GetUsagesAsync(ScopedResourceAddress secret, CancellationToken token)
    {
        using var owner = scopes.PushSystem();
        var resources = await store.ListExactAsync<ExtensionConfigurationResource>(ResourceScopeRef.Instance,
            ExtensionKinds.ExtensionConfiguration, 0, 1001, token);
        if (resources.Count > 1000) throw new ExtensionConfigurationException("configuration_inventory_limit");
        return resources.Where(r => r.Value.Definition.Bindings.Any(b => b.Secret is { } s && s.ScopeRef == secret.ScopeRef && s.Address == secret.Address))
            .Select(r => new SecretUsage(r.Value.Kind, r.Value.Name, r.Value.Name)).ToArray();
    }
}
