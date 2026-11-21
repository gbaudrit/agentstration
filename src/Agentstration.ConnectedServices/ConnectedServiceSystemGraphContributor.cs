using System.Runtime.CompilerServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.ConnectedServices;

public sealed class ConnectedServiceSystemGraphContributor(IResourceStore resources, IResourceIdentityCatalog identities) : ISystemGraphContributor
{
    public string Family => "connected-services";
    public SystemGraphContributorCoverage Coverage => new(Family, ["type-publications-schema-digests", "operation-service-generations", "parameter-secret-logical-references"], ["opaque-export-values-never-inspected"]);
    public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ConnectedServiceTypeResource>(resources, scope, ConnectedServiceResourceKinds.ConnectedServiceType, ct))
        {
            var value = stored.Value;
            yield return new([ResourceSystemGraph.Node(value)], []);
            foreach (var version in value.Definition.Versions)
            {
                var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(value, value.Uid, value.Kind, value.Name,
                    version.Version, contractHash: version.SchemaDigest, hasNativeUid: false), identities);
                builder.PublicationOf(value.Uid);
                if (version.OperationDiscovery?.Flow is { } flow) await builder.PinAsync(flow.FlowUid, "versions.operationDiscovery.flow", ct, version: flow.Version);
                yield return builder.Build();
            }
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ConnectedServiceOperationTypeResource>(resources, scope, ConnectedServiceResourceKinds.ConnectedServiceOperationType, ct))
        {
            var value = stored.Value;
            yield return new([ResourceSystemGraph.Node(value)], []);
            foreach (var version in value.Definition.Versions)
            {
                var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Publication(value, value.Uid, value.Kind, value.Name,
                    version.Version, contractHash: version.SchemaDigest, hasNativeUid: false), identities);
                builder.PublicationOf(value.Uid);
                yield return builder.Build();
            }
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ConnectedServiceResource>(resources, scope, ConnectedServiceResourceKinds.ConnectedService, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(value), identities);
            await builder.PinAsync(value.Definition.Type.TypeUid, "definition.type", ct, version: value.Definition.Type.Version);
            foreach (var binding in value.Definition.Bindings)
            {
                if (binding.Parameter is { } parameter) await builder.ValueReferenceAsync(new(parameter.ScopeRef, parameter.Address.Namespace, parameter.Address.Kind, parameter.Address.Name), $"definition.bindings[{binding.InputId}].parameter", ct);
                if (binding.Secret is { ScopeRef: { } owner } secret) await builder.ValueReferenceAsync(new(owner, secret.Address.Namespace, secret.Address.Kind, secret.Address.Name), $"definition.bindings[{binding.InputId}].secret", ct);
            }
            yield return builder.Build();
        }
        await foreach (var stored in ResourceSystemGraph.ReadAsync<ConnectedServiceOperationResource>(resources, scope, ConnectedServiceResourceKinds.ConnectedServiceOperation, ct))
        {
            var value = stored.Value;
            var builder = new SystemGraphContributionBuilder(ResourceSystemGraph.Node(value) with
            { Evidence = ResourceSystemGraph.Node(value).Evidence! with { Hash = value.Definition.DefinitionDigest, Enabled = value.Enabled } }, identities);
            await builder.PinAsync(value.Definition.Service.ServiceUid, "definition.service", ct, value.Definition.Service.Generation);
            await builder.PinAsync(value.Definition.Type.TypeUid, "definition.type", ct, version: value.Definition.Type.Version);
            yield return builder.Build();
        }
    }
}
