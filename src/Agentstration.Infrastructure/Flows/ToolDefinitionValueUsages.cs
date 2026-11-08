using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolDefinitionValueUsages(IResourceStore store, IRequestContextScopeFactory scopes)
    : IParameterUsageProvider, ISecretUsageProvider
{
    async Task<IReadOnlyList<ParameterUsage>> IParameterUsageProvider.GetUsagesAsync(ScopedResourceAddress parameter, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        return (await DefinitionsAsync(cancellationToken)).Where(value => value.Resource.Definition.Bindings.OfType<ParameterToolArgumentBinding>()
                .Any(binding => binding.Parameter.ScopeRef == parameter.ScopeRef && binding.Parameter.Address == parameter.Address))
            .Select(value => new ParameterUsage(value.Resource.Kind, value.Resource.Name, value.Resource.Definition.DisplayName, Url(value))).ToArray();
    }

    async Task<IReadOnlyList<SecretUsage>> ISecretUsageProvider.GetUsagesAsync(ScopedResourceAddress secret, CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        return (await DefinitionsAsync(cancellationToken)).Where(value => value.Resource.Definition.Bindings.OfType<SecretToolArgumentBinding>()
                .Any(binding => binding.Secret.ScopeRef == secret.ScopeRef && binding.Secret.Address == secret.Address))
            .Select(value => new SecretUsage(value.Resource.Kind, value.Resource.Name, value.Resource.Definition.DisplayName)).ToArray();
    }

    private async Task<IReadOnlyList<(ToolDefinitionResource Resource, string OwnerName)>> DefinitionsAsync(CancellationToken cancellationToken)
    {
        var drafts = (await store.ListAllAsync<ToolDefinitionResource>(ToolResourceKinds.ToolDefinition, cancellationToken)).Select(value => (value.Value, value.Value.Name));
        var versions = (await store.ListAllAsync<ToolDefinitionVersionResource>(ToolResourceKinds.ToolDefinitionVersion, cancellationToken))
            .Select(value => (new ToolDefinitionResource
            {
                ApiVersion = value.Value.ApiVersion, Kind = ToolResourceKinds.ToolDefinitionVersion, Metadata = value.Value.Metadata,
                ScopeRef = value.Value.ScopeRef, Definition = value.Value.Definition
            }, value.Value.DefinitionName));
        return drafts.Concat(versions).ToArray();
    }

    private static string Url((ToolDefinitionResource Resource, string OwnerName) value) => $"/tools/definitions/{Uri.EscapeDataString(value.OwnerName)}?namespace={Uri.EscapeDataString(value.Resource.Namespace.Value)}&context={Uri.EscapeDataString(value.Resource.ScopeRef!.Value.Value)}";
}
