using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;

namespace Agentstration.Infrastructure.DataSources;

public sealed class DataSourceValueUsages(IResourceStore store, IRequestContextScopeFactory scopes)
    : IParameterUsageProvider, ISecretUsageProvider
{
    async Task<IReadOnlyList<ParameterUsage>> IParameterUsageProvider.GetUsagesAsync(ScopedResourceAddress parameter, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        return (await SourcesAsync(cancellationToken)).Where(source => source.Definition.Dataset!.Acquisition.ParameterBindings
                .Any(binding => binding.Parameter?.ScopeRef == parameter.ScopeRef && binding.Parameter.Address == parameter.Address))
            .Select(source => new ParameterUsage(source.Kind, source.Name, source.Definition.DisplayName, Url(source))).ToArray();
    }

    async Task<IReadOnlyList<SecretUsage>> ISecretUsageProvider.GetUsagesAsync(ScopedResourceAddress secret, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        return (await SourcesAsync(cancellationToken)).Where(source => source.Definition.Dataset!.Acquisition.ParameterBindings
                .Any(binding => binding.Secret?.ScopeRef == secret.ScopeRef && binding.Secret.Address == secret.Address))
            .Select(source => new SecretUsage(source.Kind, source.Name, source.Definition.DisplayName) { Url = Url(source) }).ToArray();
    }

    private async Task<IEnumerable<DataSourceResource>> SourcesAsync(CancellationToken cancellationToken) =>
        (await store.ListAllAsync<DataSourceResource>(DataSourceResourceKinds.DataSource, cancellationToken))
            .Select(value => value.Value).Where(source => source.Definition.Dataset is not null);

    private static string Url(DataSourceResource source) =>
        $"/data-sources/{Uri.EscapeDataString(source.Name)}?namespace={Uri.EscapeDataString(source.Namespace.Value)}&context={Uri.EscapeDataString(source.ScopeRef!.Value.Value)}";
}
